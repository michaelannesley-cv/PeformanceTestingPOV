Create or Alter View [dbo].[ViewQueryStoreMostFrequentQueries]
as
SELECT TOP 100
    qsqt.query_sql_text,
    SUM(rs.count_executions) AS total_executions
FROM sys.query_store_query_text qsqt
         JOIN sys.query_store_query qsq ON qsqt.query_text_id = qsq.query_text_id
         JOIN sys.query_store_plan qsp ON qsq.query_id = qsp.query_id
         JOIN sys.query_store_runtime_stats rs ON qsp.plan_id = rs.plan_id
GROUP BY qsqt.query_sql_text
ORDER BY total_executions DESC;
