using System.Threading;
using MongoDB.Driver;

namespace MongoDBMigrations
{
    public sealed class MigrationContext
    {
        public MigrationContext(IMongoDatabase database, IClientSessionHandle session, in CancellationToken token) {
            Database = database;
            Session = session;
            CancellationToken = token;
        }
        public IMongoDatabase Database { get; }
        public IClientSessionHandle Session { get; }
        public CancellationToken CancellationToken { get; }
    }
    public interface IMigration
    {
        /// <summary>
        /// Field which consist semantic version in format MAJOR.MINOR.REVISION.
        /// </summary>
        Version Version { get; }

        /// <summary>
        /// Name of migration.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Roll forward method.
        /// </summary>
        /// <param name="context">Migration context</param>
        void Up(MigrationContext context);

        /// <summary>
        /// Roll back method.
        /// </summary>
        /// <param name="context">Migration context</param>
        void Down(MigrationContext context);
    }
}