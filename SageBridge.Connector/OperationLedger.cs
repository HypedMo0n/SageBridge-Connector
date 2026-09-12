using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using Serilog;

namespace SageBridge.Connector
{
    public enum OperationState
    {
        Pending,
        Processing,
        /// <summary>
        /// The Sage SDK write succeeded and SageRecordId is known, but the
        /// cloud has not yet confirmed receiving the terminal job result
        /// (network/process interruption between Post() and the cloud ack).
        /// Safe to retry submitting the same result; never re-run the write.
        /// </summary>
        ResultPending,
        Succeeded,
        Failed,
        Uncertain
    }

    public class OperationRecord
    {
        public long Id { get; set; }
        public string IdempotencyKey { get; set; }
        public string CompanyId { get; set; }
        public string Action { get; set; }
        public string PayloadHash { get; set; }
        public string PayloadJson { get; set; }
        public string JobId { get; set; }
        public string State { get; set; }
        public string SageRecordId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class OperationLedger : IDisposable
    {
        private readonly SQLiteConnection _connection;
        private readonly string _connectionString;
        private readonly object _lock = new object();
        private bool _disposed;

        public OperationLedger(string dbPath)
        {
            _connectionString = $"Data Source={dbPath};Version=3;";
            _connection = new SQLiteConnection(_connectionString);
            _connection.Open();
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var cmd = new SQLiteCommand(@"
                CREATE TABLE IF NOT EXISTS operation_ledger (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    idempotency_key TEXT NOT NULL,
                    company_id TEXT NOT NULL,
                    action TEXT NOT NULL,
                    payload_hash TEXT NOT NULL,
                    payload_json TEXT,
                    job_id TEXT,
                    state TEXT NOT NULL DEFAULT 'pending',
                    sage_record_id TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    completed_at TEXT,
                    UNIQUE(idempotency_key, company_id)
                );
                CREATE INDEX IF NOT EXISTS idx_idempotency_key ON operation_ledger(idempotency_key);
                CREATE INDEX IF NOT EXISTS idx_state ON operation_ledger(state);
            ", _connection);
            cmd.ExecuteNonQuery();
        }

        public static string ComputeHash(string action, JObject payload)
        {
            // Canonicalize: sort keys recursively, normalize whitespace
            var canonical = CanonicalizePayload(action, payload);
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToBase64String(bytes);
        }

        private static string CanonicalizePayload(string action, JObject payload)
        {
            var sb = new StringBuilder();
            sb.Append(action.ToLowerInvariant());
            sb.Append("|");
            if (payload != null)
            {
                AppendSortedProperties(payload, sb);
            }
            return sb.ToString();
        }

        private static void AppendSortedProperties(JObject obj, StringBuilder sb)
        {
            var keys = new System.Collections.Generic.List<string>();
            foreach (var prop in obj.Properties())
                keys.Add(prop.Name);
            keys.Sort(StringComparer.Ordinal);

            foreach (var key in keys)
            {
                var value = obj[key];
                sb.Append(key.ToLowerInvariant());
                sb.Append(":");
                if (value is JObject childObj)
                {
                    sb.Append("{");
                    AppendSortedProperties(childObj, sb);
                    sb.Append("}");
                }
                else if (value is JArray array)
                {
                    sb.Append("[");
                    foreach (var item in array)
                    {
                        if (item is JObject itemObj)
                            AppendSortedProperties(itemObj, sb);
                        else
                            sb.Append(item?.ToString().Trim() ?? "");
                    }
                    sb.Append("]");
                }
                else
                {
                    sb.Append(value?.ToString().Trim() ?? "");
                }
                sb.Append("|");
            }
        }

        public OperationRecord GetOperation(string idempotencyKey, string companyId)
        {
            lock (_lock)
            {
                using var cmd = new SQLiteCommand(@"
                    SELECT id, idempotency_key, company_id, action, payload_hash, payload_json, job_id, state, sage_record_id, created_at, completed_at
                    FROM operation_ledger
                    WHERE idempotency_key = @key AND company_id = @company
                ", _connection);
                cmd.Parameters.AddWithValue("@key", idempotencyKey);
                cmd.Parameters.AddWithValue("@company", companyId);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new OperationRecord
                    {
                        Id = reader.GetInt64(0),
                        IdempotencyKey = reader.GetString(1),
                        CompanyId = reader.GetString(2),
                        Action = reader.GetString(3),
                        PayloadHash = reader.GetString(4),
                        PayloadJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                        JobId = reader.IsDBNull(6) ? null : reader.GetString(6),
                        State = reader.GetString(7),
                        SageRecordId = reader.IsDBNull(8) ? null : reader.GetString(8),
                        CreatedAt = reader.GetDateTime(9),
                        CompletedAt = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10)
                    };
                }
                return null;
            }
        }

        public OperationRecord CreateOperation(string idempotencyKey, string companyId, string action, string payloadHash, string payloadJson, string jobId)
        {
            lock (_lock)
            {
                using var cmd = new SQLiteCommand(@"
                    INSERT INTO operation_ledger (idempotency_key, company_id, action, payload_hash, payload_json, job_id, state, created_at)
                    VALUES (@key, @company, @action, @hash, @payload, @jobId, 'processing', datetime('now'));
                    SELECT last_insert_rowid();
                ", _connection);
                cmd.Parameters.AddWithValue("@key", idempotencyKey);
                cmd.Parameters.AddWithValue("@company", companyId);
                cmd.Parameters.AddWithValue("@action", action);
                cmd.Parameters.AddWithValue("@hash", payloadHash);
                cmd.Parameters.AddWithValue("@payload", payloadJson ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@jobId", jobId ?? (object)DBNull.Value);

                var id = (long)cmd.ExecuteScalar();
                return GetOperation(idempotencyKey, companyId);
            }
        }

        public void UpdateSageRecordId(string idempotencyKey, string companyId, string sageRecordId)
        {
            lock (_lock)
            {
                using var cmd = new SQLiteCommand(@"
                    UPDATE operation_ledger
                    SET sage_record_id = @sageId
                    WHERE idempotency_key = @key AND company_id = @company
                ", _connection);
                cmd.Parameters.AddWithValue("@sageId", sageRecordId);
                cmd.Parameters.AddWithValue("@key", idempotencyKey);
                cmd.Parameters.AddWithValue("@company", companyId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// The Sage write is confirmed done (SageRecordId is already set via
        /// UpdateSageRecordId) but the cloud has not yet acknowledged the
        /// terminal result. Distinct from 'processing' so a restart's
        /// reconciliation retries delivering the known result instead of
        /// re-checking Sage for existence.
        /// </summary>
        public void MarkResultPending(string idempotencyKey, string companyId)
        {
            lock (_lock)
            {
                using var cmd = new SQLiteCommand(@"
                    UPDATE operation_ledger
                    SET state = 'result_pending'
                    WHERE idempotency_key = @key AND company_id = @company
                ", _connection);
                cmd.Parameters.AddWithValue("@key", idempotencyKey);
                cmd.Parameters.AddWithValue("@company", companyId);
                cmd.ExecuteNonQuery();
            }
        }

        public void MarkSucceeded(string idempotencyKey, string companyId)
        {
            lock (_lock)
            {
                using var cmd = new SQLiteCommand(@"
                    UPDATE operation_ledger
                    SET state = 'succeeded', completed_at = datetime('now')
                    WHERE idempotency_key = @key AND company_id = @company
                ", _connection);
                cmd.Parameters.AddWithValue("@key", idempotencyKey);
                cmd.Parameters.AddWithValue("@company", companyId);
                cmd.ExecuteNonQuery();
            }
        }

        public void MarkFailed(string idempotencyKey, string companyId)
        {
            lock (_lock)
            {
                using var cmd = new SQLiteCommand(@"
                    UPDATE operation_ledger
                    SET state = 'failed', completed_at = datetime('now')
                    WHERE idempotency_key = @key AND company_id = @company
                ", _connection);
                cmd.Parameters.AddWithValue("@key", idempotencyKey);
                cmd.Parameters.AddWithValue("@company", companyId);
                cmd.ExecuteNonQuery();
            }
        }

        public void MarkUncertain(string idempotencyKey, string companyId)
        {
            lock (_lock)
            {
                using var cmd = new SQLiteCommand(@"
                    UPDATE operation_ledger
                    SET state = 'uncertain'
                    WHERE idempotency_key = @key AND company_id = @company
                ", _connection);
                cmd.Parameters.AddWithValue("@key", idempotencyKey);
                cmd.Parameters.AddWithValue("@company", companyId);
                cmd.ExecuteNonQuery();
            }
        }

        public List<OperationRecord> GetOperationsByState(string state)
        {
            lock (_lock)
            {
                var results = new List<OperationRecord>();
                using var cmd = new SQLiteCommand(@"
                    SELECT id, idempotency_key, company_id, action, payload_hash, payload_json, job_id, state, sage_record_id, created_at, completed_at
                    FROM operation_ledger
                    WHERE state = @state
                ", _connection);
                cmd.Parameters.AddWithValue("@state", state);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new OperationRecord
                    {
                        Id = reader.GetInt64(0),
                        IdempotencyKey = reader.GetString(1),
                        CompanyId = reader.GetString(2),
                        Action = reader.GetString(3),
                        PayloadHash = reader.GetString(4),
                        PayloadJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                        JobId = reader.IsDBNull(6) ? null : reader.GetString(6),
                        State = reader.GetString(7),
                        SageRecordId = reader.IsDBNull(8) ? null : reader.GetString(8),
                        CreatedAt = reader.GetDateTime(9),
                        CompletedAt = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10)
                    });
                }
                return results;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _connection?.Close();
                _connection?.Dispose();
                _disposed = true;
            }
        }
    }
}
