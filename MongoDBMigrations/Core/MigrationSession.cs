using System.Threading;
using MongoDB.Driver;
using MongoDBMigrations.Core.Contracts;

namespace MongoDBMigrations.Core
{
    public sealed class SimpleMigrationSession : IMigrationSession
    {
        public IMigrationTransaction BeginTransaction() => new SimpleTransaction();

        sealed class SimpleTransaction : IMigrationTransaction
        {
            public void Dispose() { }
            public IClientSessionHandle Session => null;
            public void Commit(CancellationToken _) { }
        }
    }
    public sealed class MongoMigrationSession : IMigrationSession
    {
        readonly IMongoClient client;
        public MongoMigrationSession(IMongoClient client) {
            this.client = client;
        }
        public IMigrationTransaction BeginTransaction() => new MongoMigrationTransaction(client.StartSession());

        sealed class MongoMigrationTransaction : IMigrationTransaction
        {
            readonly IClientSessionHandle session;

            public MongoMigrationTransaction(IClientSessionHandle session) {
                this.session = session;
                session.StartTransaction();
            }
            public void Dispose() => session.Dispose();
            public IClientSessionHandle Session => session;
            public void Commit(CancellationToken token) => session.CommitTransaction(token);
        }
    }
}