using System;
using System.Threading;
using MongoDB.Driver;

namespace MongoDBMigrations.Core.Contracts
{
    public interface IMigrationTransaction : IDisposable
    {
        IClientSessionHandle Session { get; }
        void Commit(CancellationToken token = default);
    }
    public interface IMigrationSession
    {
        IMigrationTransaction BeginTransaction();
    }
}