using System;
using System.Collections.Generic;
using MongoDB.Driver;
using MongoDB.Bson.Serialization;
using MongoDBMigrations.Core;
using System.Reflection;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using MongoDBMigrations.Document;
using Renci.SshNet;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using MongoDBMigrations.Core.Contracts;

namespace MongoDBMigrations
{
    public sealed class MigrationEngine : ILocator, ISchemeValidation, IMigrationRunner
    {
        internal class SshConfig
        {
            public SshClient SshClient;
            public ForwardedPortLocal ForwardedPortLocal;
            public uint BoundPort;
            public string BoundHost;
        }

        private const string LOCALHOST = "127.0.0.1";

        private IMongoDatabase _database;
        private MigrationManager _locator;
        private DatabaseManager _status;
        private bool _schemeValidationNeeded;
        private string _migrationProjectLocation;
        private CancellationToken _token;
        private readonly IList<Action<InterimMigrationResult>> _progressHandlers = new List<Action<InterimMigrationResult>>();
        private bool _useTransaction;

        private SshConfig _sshConfig;
        private SslSettings _tlsSettings;

        static MigrationEngine()
        {
            BsonSerializer.RegisterSerializer(typeof(Version), new VerstionStructSerializer());
        }

        public ILocator UseDatabase(string connectionString, string databaseName, MongoEmulationEnum emulation = MongoEmulationEnum.None)
        {
            var setting = MongoClientSettings.FromConnectionString(connectionString);
            var client = new MongoClient(setting);
            return UseDatabase(client, databaseName, emulation);
        }

        public ILocator UseDatabase(IMongoClient mongoClient, string databaseName, MongoEmulationEnum emulation = MongoEmulationEnum.None)
        {
            var database = mongoClient
                .SetTls(_tlsSettings)
                .SetSsh(_sshConfig)
                .GetDatabase(databaseName);
            return new MigrationEngine
            {
                _database = database,
                _locator = new MigrationManager(),
                _status = new DatabaseManager(database, emulation)
            };
        }

        public ILocator UseTransaction() {
            _useTransaction = true;
            return this;
        }

        private MigrationEngine EstablishConnectionViaSsh(SshClient client, ServerAdressConfig mongoAdress)
        {
            client.Connect();
            var forwardedPortLocal = new ForwardedPortLocal(LOCALHOST, mongoAdress.Host, mongoAdress.Port);
            client.AddForwardedPort(forwardedPortLocal);
            forwardedPortLocal.Start();

            _sshConfig = new SshConfig
            {
                SshClient = client,
                ForwardedPortLocal = forwardedPortLocal,
                BoundPort = forwardedPortLocal.BoundPort,
                BoundHost = LOCALHOST
            };

            return this;
        }

        public MigrationEngine UseTls(X509Certificate2 certificate)
        {
            if (certificate == null)
                throw new ArgumentNullException(nameof(certificate));

            _tlsSettings = new SslSettings
            {
                ClientCertificates = new[] { certificate },
            };
            return this;
        }

        public MigrationEngine UseSshTunnel(ServerAdressConfig sshAdress, string sshUser, string sshPassword, ServerAdressConfig mongoAdress)
        { 
            return EstablishConnectionViaSsh(new SshClient(sshAdress.Host, sshAdress.PortAsInt, sshUser, sshPassword), mongoAdress);
        }

        public MigrationEngine UseSshTunnel(ServerAdressConfig sshAdress, string sshUser, Stream privateKeyFileStream, ServerAdressConfig mongoAdress, string keyFilePassPhrase = null)
        {
            var keyFile = keyFilePassPhrase == null ? new PrivateKeyFile(privateKeyFileStream) : new PrivateKeyFile(privateKeyFileStream, keyFilePassPhrase);
            return EstablishConnectionViaSsh(new SshClient(sshAdress.Host, sshAdress.PortAsInt, sshUser, new[] { keyFile }), mongoAdress);
        }

        public IMigrationRunner UseProgressHandler(Action<InterimMigrationResult> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            this._progressHandlers.Add(action);

            return this;
        }

        private MigrationResult RunInternal(Version version)
        {
            try
            {
                var currentDatabaseVersion = _status.GetVersion();
                var migrations = _locator.GetMigrations(currentDatabaseVersion, version);

                var result = new MigrationResult
                {
                    ServerAdress = string.Join(",", _database.Client.Settings.Servers),
                    DatabaseName = _database.DatabaseNamespace.DatabaseName,
                    InterimSteps = new List<InterimMigrationResult>(),
                    Success = true
                };

                if (!migrations.Any())
                {
                    result.CurrentVersion = currentDatabaseVersion;
                    return result;
                }

                if (_token.IsCancellationRequested)
                {
                    _token.ThrowIfCancellationRequested();
                }

                var isUp = version > currentDatabaseVersion;

                if (_schemeValidationNeeded)
                {
                    var validator = new MongoSchemeValidator();
                    var validationResult = validator.Validate(migrations, isUp, _migrationProjectLocation, _database);
                    if (validationResult.FailedCollections.Any())
                    {
                        result.Success = false;
                        var failedCollections = string.Join(Environment.NewLine, validationResult.FailedCollections);
                        throw new InvalidOperationException($"Some schema validation issues found in: {failedCollections}");
                    }
                }

                int counter = 0;
                var session = _useTransaction ? (IMigrationSession) new MongoMigrationSession(_database.Client) : new SimpleMigrationSession();
                
                using (var transaction = session.BeginTransaction()) {
                    var context = new MigrationContext(_database, transaction.Session, _token);
                    foreach (var m in migrations) {
                        if (_token.IsCancellationRequested) {
                            result.Success = false;
                            _token.ThrowIfCancellationRequested();
                        }

                        counter++;
                        var increment = new InterimMigrationResult();

                        try {
                            if (isUp)
                                m.Up(context);
                            else
                                m.Down(context);

                            var insertedMigration = _status.SaveMigration(m, isUp, transaction.Session);

                            increment.MigrationName = insertedMigration.Name;
                            increment.TargetVersion = insertedMigration.Ver;
                            increment.ServerAdress = result.ServerAdress;
                            increment.DatabaseName = result.DatabaseName;
                            increment.CurrentNumber = counter;
                            increment.TotalCount = migrations.Count;
                            result.InterimSteps.Add(increment);
                        }
                        catch (Exception ex) {
                            result.Success = false;
                            throw new InvalidOperationException("Something went wrong during migration", ex);
                        }
                        finally {
                            foreach (var action in _progressHandlers) {
                                action(increment);
                            }
                            result.CurrentVersion = _status.GetVersion();
                        }
                    }
                    transaction.Commit(_token);
                }
                return result;
            }
            finally
            {
                if (_sshConfig != null && _sshConfig.SshClient.IsConnected)
                {
                    _sshConfig.SshClient.Dispose();
                    _sshConfig.ForwardedPortLocal.Dispose();
                }
            }
        }

        public MigrationResult Run(Version version) =>
            _token.CanBeCanceled
                ? Task.Factory.StartNew(() => RunInternal(version), _token)
                      .ConfigureAwait(false)
                      .GetAwaiter()
                      .GetResult()
                : RunInternal(version);

        public MigrationResult Run()
        {
            var targetVersion = this._locator.GetNewestLocalVersion();
            return Run(targetVersion);
        }

        public ISchemeValidation UseAssembly(Assembly assembly)
        {
            if (assembly == null)
                throw new ArgumentNullException(nameof(assembly));
            this._locator.SetAssembly(assembly);
            return this;
        }

        public ISchemeValidation UseAssemblyOfType(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            this._locator.LookInAssemblyOfType(type);
            return this;
        }

        public ISchemeValidation UseAssemblyOfType<T>()
        {
            this._locator.LookInAssemblyOfType<T>();
            return this;
        }

        public IMigrationRunner UseCancelationToken(CancellationToken token)
        {
            if (!token.CanBeCanceled)
                throw new ArgumentException($"Invalid token or it's canceled already.", nameof(token));
            this._token = token;
            return this;
        }

        public IMigrationRunner UseSchemeValidation(bool enabled, string location)
        {
            this._schemeValidationNeeded = enabled;
            if (enabled)
            {
                if (string.IsNullOrEmpty(location))
                    throw new ArgumentNullException(nameof(location));
                this._migrationProjectLocation = location;
            }
            return this;
        }

        public IMigrationRunner UseCustomSpecificationCollectionName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));
            _status.SpecCollectionName = name;


            return this;
        }
    }
}