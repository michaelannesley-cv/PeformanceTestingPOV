using System.Data;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using Newtonsoft.Json;
using OpenQA.Selenium;

namespace PerformanceTestingPOV
{
    internal class TestSiteSetup
    {
        private const string DbNamePlaceHolder = "XXDBNameXX";

        private TestInfo _testInfo;
        
        private TestSiteSetup(TestInfo testInfo)
        {
            this._testInfo = testInfo;
        }

        public static TestSiteSetup GetTestInfo(TestSitesEnum testSite)
        {
            var jsonPath = Path.Combine(AppContext.BaseDirectory, "TestInfos.json");
            var jsonTestInfos = File.ReadAllText(jsonPath);
            var testInfos = JsonConvert.DeserializeObject<List<TestInfo>>(jsonTestInfos);

            if (testInfos == null)
            {
                throw new InvalidOperationException("TestInfos could not be deserialized from JSON.");
            }

            var foundTestInfo = testInfos.FirstOrDefault(x => x.SiteName == testSite.ToString());
            
            if (foundTestInfo == null)
            {
                throw new InvalidOperationException($"TestInfo for {testSite} could not be found.");
            }

            return new TestSiteSetup(foundTestInfo);
        }

        public TestSiteSetup SetQueryStoreOn()
        {
            ExcecuteQueryAgainstDb(_testInfo.DbName, _testInfo.DbConnectionString, "QueryStoreOn");
            return this;
        }

        public TestSiteSetup SetQueryStoreClear()
        {
            ExcecuteQueryAgainstDb(_testInfo.DbName, _testInfo.DbConnectionString, "QueryStoreClear");
            return this;
        }
        
        public TestSiteSetup SetViewQueryStoreMostExpensiveQueries()
        {
            ExcecuteQueryAgainstDb(_testInfo.DbConnectionString, "ViewQueryStoreMostExpensiveQueries");
            return this;
        }
        
        public TestSiteSetup SetViewQueryStoreMostFrequentQueries()
        {
            ExcecuteQueryAgainstDb(_testInfo.DbConnectionString, "ViewQueryStoreMostFrequentQueries");
            return this;
        }
        
        public TestInfo GetTestInfo()
        {
            return _testInfo;
        }
        
        public TestSiteSetup WarmUpSite(Action<IWebDriver, string> action, IWebDriver driver)
        {
            action(driver, _testInfo.Url);
            return this;
        }
        
        public TestSiteSetup ConfirmTestInfoPopulated()
        {
            if (_testInfo == null)
                throw new ArgumentNullException(nameof(_testInfo));
            return this;
        }
        
        public TestSiteSetup GetQueryStoreMostFrequentQueries()
        {
            var sqlCommandText = "Select query_sql_text as [Sql], total_executions as Executions from ViewQueryStoreMostFrequentQueries";
            var dataTable = GetDataFromDb(sqlCommandText);
            var dataTableFromBenchMark = LoadCsvToDataTable("MostFrequentQueries");
            dataTableFromBenchMark.Columns[0].ColumnName = "Sql";
            dataTableFromBenchMark.Columns[1].ColumnName = "Executions";
            
            // Do something with this data table
            // I think what I am going to do is to take the data in the base line
            // and compare it to what we have just gotten. 
            // Find the same queries and compare the execution times and cpu times
            return this;
        }
        
        public TestSiteSetup GetQueryStoreMostExpensiveQueries()
        {
            var sqlCommandText = "Select query_sql_text as [Sql], Executions as Excecutions, total_duration_ms as TotalDurationMS, avg_duration_ms as AverageDurationMS  from ViewQueryStoreMostExpensiveQueries";
            var dataTable = GetDataFromDb(sqlCommandText);
            dataTable.Columns[0].ColumnName = "Sql";
            dataTable.Columns[1].ColumnName = "Executions";
            dataTable.Columns[2].ColumnName = "TotalDurationMS";
            dataTable.Columns[3].ColumnName = "AverageDurationMS";
            dataTable.Columns.Add("HashedSql");
            
            foreach (DataRow row in dataTable.Rows)
            {
                var outputStringFromDb = CleanAndHash(row.Field<string>(0) ?? throw new InvalidOperationException("No SQL statement found"));
                row["HashedSql"] = outputStringFromDb;
            }
            
            
            var dataTableFromBenchMark = LoadCsvToDataTable("MostExpensiveQueries");
            dataTableFromBenchMark.Columns[0].ColumnName = "Sql";
            dataTableFromBenchMark.Columns[1].ColumnName = "Executions";
            dataTableFromBenchMark.Columns[2].ColumnName = "TotalDurationMS";
            dataTableFromBenchMark.Columns[3].ColumnName = "AverageDurationMS";
            dataTableFromBenchMark.Columns.Add("HashedSql");

            foreach (DataRow row in dataTableFromBenchMark.Rows)
            {
                // get the hashed string so easy to compare
                var outputStringFromCsv = CleanAndHash( row.Field<string>(0) ?? throw new InvalidOperationException("No SQL statement found"));
                row["HashedSql"] = outputStringFromCsv;
            }
            
            var comparedExecutionsTable = GenerateAComparedExecutionsTable(dataTableFromBenchMark, dataTable);
            
            // Do something with this data table
            return this;
        }
        
        private static DataTable GenerateAComparedExecutionsTable(DataTable benchmarkTable, DataTable currentTable)
        {
            // Create the result DataTable
            DataTable resultTable = new DataTable();
            resultTable.Columns.Add("HashedSql", typeof(string));
            resultTable.Columns.Add("BenchMarkExecutions", typeof(Int64));
            resultTable.Columns.Add("CurrentExecutions", typeof(Int64));

            // Use LINQ to join both tables on HashedSql
            var query = from benchRow in benchmarkTable.AsEnumerable()
                join currentRow in currentTable.AsEnumerable()
                    on benchRow.Field<string>("HashedSql") equals currentRow.Field<string>("HashedSql")
                select new
                {
                    HashedSql = benchRow.Field<string>("HashedSql"),
                    BenchMarkExecutions = Int64.Parse(benchRow.Field<string>("Executions")),
                    CurrentExecutions = currentRow.Field<Int64>("Executions")
                };

            // Fill result table
            foreach (var row in query)
            {
                resultTable.Rows.Add(row.HashedSql, row.BenchMarkExecutions, row.CurrentExecutions);
            }

            var jsonData = ConvertDataTableToJson(resultTable);

            return resultTable;
        }
        
        /// <summary>
        /// Converts the comparison results to a JSON string
        /// </summary>
        /// <param name="dataTable">DataTable to convert to JSON</param>
        /// <param name="formatting">Optional: Set to true for indented JSON</param>
        /// <returns>JSON representation of the data</returns>
        public static string ConvertDataTableToJson(DataTable dataTable, bool formatting = true)
        {
            return JsonConvert.SerializeObject(dataTable, formatting ? Formatting.Indented : Formatting.None);
        }
        
        private static string CleanAndHash(string input)
        {
            // Remove all whitespace, line breaks, and returns
            string cleaned = Regex.Replace(input, @"\s+", "");

            // Compute SHA256 hash
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(cleaned);
                byte[] hash = sha256.ComputeHash(bytes);

                // Convert hash to hex string
                StringBuilder sb = new StringBuilder();
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private DataTable LoadCsvToDataTable(string csvFileName)
        {
            var dataTable = new DataTable();
            string folderPath = Path.Combine(Directory.GetCurrentDirectory(), "BenchMarks");
            string fullPath = Path.Combine(folderPath, $"{csvFileName}.csv");

            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"CSV file not found: {fullPath}");

            using (var parser = new TextFieldParser(fullPath))
            {
                parser.TextFieldType = FieldType.Delimited;
                parser.SetDelimiters(",");
                bool isFirstRow = true;
                while (!parser.EndOfData)
                {
                    string[] fields = parser.ReadFields();
                    if (fields == null) continue;
                    if (isFirstRow)
                    {
                        foreach (var header in fields)
                            dataTable.Columns.Add(header);
                        isFirstRow = false;
                    }
                    else
                    {
                        dataTable.Rows.Add(fields);
                    }
                }
            }
            return dataTable;
        }

        private DataTable GetDataFromDb(string sqlCommandText)
        {
            using var connection = new SqlConnection(_testInfo.DbConnectionString);
            using var command = new SqlCommand(sqlCommandText, connection);
            using var adapter = new SqlDataAdapter(command);
            var dataTable = new DataTable();
            connection.Open();
            adapter.Fill(dataTable);
            return dataTable;
        }

        private static void ExcecuteQueryAgainstDb(string dbConnectionString, string sqlTextFileName)
        {
            if (string.IsNullOrWhiteSpace(dbConnectionString))
                throw new ArgumentException("TestInfo does not contain a valid database connection string.");
            
            var sqlCommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SqlScripts", $"{sqlTextFileName}.sql"));

            using var connection = new SqlConnection(dbConnectionString);
            using var command = new SqlCommand(sqlCommandText, connection);
            connection.Open();
            command.ExecuteNonQuery();
        }
        
        private static void ExcecuteQueryAgainstDb(string dbName, string dbConnectionString, string sqlTextFileName)
        {
            if (string.IsNullOrWhiteSpace(dbConnectionString))
                throw new ArgumentException("TestInfo does not contain a valid database connection string.");

            using var connection = new SqlConnection(dbConnectionString);
            using var command = new SqlCommand(GetSqlText(dbName, sqlTextFileName), connection);
            connection.Open();
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Reads the SQL file specified by <paramref name="fileName"/> and replaces any occurrences of <see cref="DbNamePlaceHolder"/> with the actual database name.
        /// </summary>
        /// <param name="dbName">The name of the database to replace the placeholder with.</param>
        /// <param name="fileName">The name of the SQL file to read, without the .sql extension.</param>
        /// <returns>The contents of the SQL file with the placeholder replaced.</returns>
        private static string GetSqlText(string dbName, string fileName)
        {
            return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SqlScripts", $"{fileName}.sql"))
                .Replace(DbNamePlaceHolder, dbName);
        }
    }
}
