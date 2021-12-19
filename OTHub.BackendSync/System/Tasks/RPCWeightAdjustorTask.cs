﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using OTHub.BackendSync.Logging;
using OTHub.Settings;

namespace OTHub.BackendSync.System.Tasks
{
    public class RPCWeightAdjustorTask : TaskRunGeneric
    {
        public RPCWeightAdjustorTask() : base("RPC Weight Algorithm")
        {
        }

        public override async Task Execute(Source source)
        {
            await using (MySqlConnection connection =
                new MySqlConnection(OTHubSettings.Instance.MariaDB.ConnectionString))
            {
                RPCModel[] data = (await connection.QueryAsync<RPCModel>(@"SELECT r.ID, r.`Name`, r.LatestBlockNumber BlockNumber, r.LastCalculatedDailyScore, r.Weight, b.DisplayName BlockchainName,
b.id BlockchainID,
SUM(CASE WHEN h.Timestamp >= DATE_Add(NOW(), INTERVAL -100 DAY) THEN 1 ELSE 0 END) DailyRequestsTotal,
SUM(CASE WHEN h.Success = 1 AND h.Timestamp >= DATE_Add(NOW(), INTERVAL -100 DAY) THEN 1 ELSE 0 END) DailySuccessTotal
FROM rpcs r
JOIN blockchains b ON b.ID = r.BlockchainID
LEFT JOIN rpcshistory h ON h.RPCID = r.ID
WHERE r.EnabledByUser = 1
GROUP BY r.`Name`, r.LatestBlockNumber, r.Weight, b.DisplayName
ORDER BY b.ID, r.ID")).ToArray();

                
                foreach (IGrouping<string, RPCModel> group in data.GroupBy(d => d.BlockchainName))
                {
                    ulong? maxBlockNumber = group.Max(g => g.BlockNumber);

                    foreach (RPCModel row in group)
                    {
                        decimal score = 0;

                        if (row.DailyRequestsTotal > 0)
                        {
                            score = (Math.Round(((row.DailySuccessTotal / row.DailyRequestsTotal) * 100) * 100, 2) /
                                     100);
                        }

                        int weight = row.Weight;

                        if (row.LastCalculatedDailyScore.HasValue)
                        {
                            if (maxBlockNumber.Value - row.BlockNumber > 5000)
                            {
                                weight = 0;
                            }
                            else
                            {
                                weight = (int)Math.Round((double)(100m - ((100m - score) * 5m)), MidpointRounding.ToEven);
                            }
                        }

                        if (weight > 100)
                        {
                            weight = 100;
                        }
                        else if (weight < 0)
                        {
                            weight = 0;
                        }

                        await connection.ExecuteAsync(
                            @"UPDATE rpcs SET Weight = @weight, LastCalculatedDailyScore = @lastScore WHERE ID = @id",
                            new
                            {
                                row.ID,
                                weight = weight,
                                lastScore = score
                            });
                    }
                }
            }
        }

        private class RPCModel
        {
            public int ID { get; set; }
            public string Name { get; set; }
            public UInt64 BlockNumber { get; set; }
            public int Weight { get; set; }
            public int BlockchainID { get; set; }
            public string BlockchainName { get; set; }

            public long DailyRequestsTotal { get; set; }
            public decimal DailySuccessTotal { get; set; }

            public int? LastCalculatedDailyScore { get; set; }
        }
    }
}