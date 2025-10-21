using System;
using System.Data;
using Microsoft.Data.SqlClient;

namespace misas_thai_api
{
    public static class Database
    {
        public static IDbConnection GetOpenConnection()
        {
            var cs = Environment.GetEnvironmentVariable("AzureSqlDatabase__ConnectionString");
            if (string.IsNullOrWhiteSpace(cs))
            {
                throw new InvalidOperationException("AzureSqlDatabase__ConnectionString environment variable is not set.");
            }
            var conn = new SqlConnection(cs);
            conn.Open();
            return conn;
        }
    }
}
