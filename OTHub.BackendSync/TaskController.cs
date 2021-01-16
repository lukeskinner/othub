﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using OTHub.BackendSync.Database.Models;
using OTHub.BackendSync.Logging;
using OTHub.Settings;

namespace OTHub.BackendSync
{
    public class TaskController
    {
        private readonly Blockchain _blockchain;
        private readonly Network _network;
        private readonly Source _source;
        private readonly ConcurrentBag<TaskControllerItem> _items = new ConcurrentBag<TaskControllerItem>();

        private class TaskControllerItem
        {
            private readonly Blockchain _blockchain;
            private readonly Network _network;
            private readonly Source _source;
            private readonly TaskRun _task;
            private readonly TimeSpan _runEveryTimeSpan;
            private DateTime _lastRunDateTime;
            private SystemStatus _systemStatus;

            internal TaskControllerItem(Blockchain blockchain, Network network, Source source, TaskRun task, TimeSpan runEveryTimeSpan, bool startNow)
            {
                _blockchain = blockchain;
                _network = network;
                _source = source;
                _task = task;
                _runEveryTimeSpan = runEveryTimeSpan;
                _lastRunDateTime = startNow ? DateTime.MinValue : DateTime.Now;
                _systemStatus = new SystemStatus(task.Name);

                using (var connection = new MySqlConnection(OTHubSettings.Instance.MariaDB.ConnectionString))
                {
                    _systemStatus.InsertOrUpdate(connection, null, NextRunDate, false);
                }
            }

            public bool NeedsRunning
            {
                get { return ((DateTime.Now - _lastRunDateTime) > _runEveryTimeSpan); }
            }

            public DateTime? NextRunDate
            {
                get
                {
                    if (_lastRunDateTime == DateTime.MinValue)
                        return null;

                    return _lastRunDateTime + _runEveryTimeSpan;
                }
            }

            public async Task Execute()
            {
                DateTime startTime = DateTime.Now;

                bool success = false;

                try
                {
                    Logger.WriteLine(_source, "Starting " + _task.Name);

                    using (var connection = new MySqlConnection(OTHubSettings.Instance.MariaDB.ConnectionString))
                    {
                        _systemStatus.InsertOrUpdate(connection, true, null, true);
                    }

                    await _task.Execute(_source, _blockchain, _network);

                    success = true;
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(_source, ex.ToString());
                }
                finally
                {
                    _lastRunDateTime = DateTime.Now;
                    using (var connection = new MySqlConnection(OTHubSettings.Instance.MariaDB.ConnectionString))
                    {
                        _systemStatus.InsertOrUpdate(connection, success, NextRunDate, false);
                    }
                    Logger.WriteLine(_source, "Finished " + _task.Name + " in " + (DateTime.Now - startTime).TotalSeconds + " seconds");
                }
            }
        }

        public void Schedule(TaskRun task, TimeSpan runEveryTimeSpan, bool startNow)
        {
            using (var connection = new MySqlConnection(OTHubSettings.Instance.MariaDB.ConnectionString))
            {
                var blockchains = connection.Query(@"SELECT * FROM blockchains").ToArray();

                foreach (var blockchain in blockchains)
                {
                    int id = blockchain.ID;
                    string blockchainName = blockchain.BlockchainName;
                    string networkName = blockchain.NetworkName;

                    Blockchain blockchainEnum = Enum.Parse<Blockchain>(blockchainName);
                    Network networkNameEnum = Enum.Parse<Network>(networkName);

                    var item = new TaskControllerItem(blockchainEnum, networkNameEnum, _source, task, runEveryTimeSpan, startNow);
                    _items.Add(item);
                }
            }
        }

        private bool _showSleepingLogMessage = true;
        private bool isFirstSync = true;

        public TaskController(Source source)
        {
            _source = source;
        }

        public void Start()
        {
            while (true)
            {
                if (isFirstSync)
                {
                    isFirstSync = false;
                }

                var items = _items.Where(i => i.NeedsRunning).Reverse().ToArray();

                foreach (var taskControllerItem in items)
                {
                    taskControllerItem.Execute().GetAwaiter().GetResult();
                }

                if (!items.Any())
                {
                    if (_showSleepingLogMessage)
                    {
                        _showSleepingLogMessage = false;
                        Logger.WriteLine(_source, "Sleeping...");
                    }

                    Thread.Sleep(2000);
                }
                else
                {
                    _showSleepingLogMessage = true;
                }
            }
        }
    }
}