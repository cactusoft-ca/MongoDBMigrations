﻿using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDBMigrations.SmokeTests.Migrations
{
    public class _1_1_0_SecondMigrationTest : IMigration
    {
        public Version Version => new Version(1, 1, 0);

        public string Name => "Changing type of age type";

        public void Down(MigrationContext context)
        {
            var collection = context.Database.GetCollection<BsonDocument>("clients");
            var list = collection.Find(FilterDefinition<BsonDocument>.Empty).ToList();
            FieldDefinition<BsonDocument, int> fieldDefenition = "age";
            foreach (var item in list)
            {
                collection.UpdateOne(new BsonDocument("_id", item["_id"]),
                    Builders<BsonDocument>.Update.Set(fieldDefenition, item["age"].ToInt32()));
            }
        }

        public void Up(MigrationContext context)
        {
            var collection = context.Database.GetCollection<BsonDocument>("clients");
            var list = collection.Find(FilterDefinition<BsonDocument>.Empty).ToList();
            FieldDefinition<BsonDocument, string> fieldDefenition = "age";
            foreach (var item in list)
            {
                collection.UpdateOne(new BsonDocument("_id", item["_id"]),
                    Builders<BsonDocument>.Update.Set(fieldDefenition, item["age"].ToString()));
            }
        }
    }
}