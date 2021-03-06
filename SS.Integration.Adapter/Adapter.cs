//Copyright 2014 Spin Services Limited

//Licensed under the Apache License, Version 2.0 (the "License");
//you may not use this file except in compliance with the License.
//You may obtain a copy of the License at

//    http://www.apache.org/licenses/LICENSE-2.0

//Unless required by applicable law or agreed to in writing, software
//distributed under the License is distributed on an "AS IS" BASIS,
//WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//See the License for the specific language governing permissions and
//limitations under the License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using SS.Integration.Adapter.Interface;
using SS.Integration.Adapter.ProcessState;
using log4net;
using SportingSolutions.Udapi.Sdk.Interfaces;
using SS.Integration.Adapter.Model;
using SS.Integration.Adapter.Model.Interfaces;
using SS.Integration.Common.Stats;
using SS.Integration.Common.Stats.Interface;
using SS.Integration.Common.Stats.Keys;

namespace SS.Integration.Adapter
{
    using Model.Enums;
    using System.Diagnostics;

    public class Adapter
    {
        private readonly static object _sync = new object();
        private readonly ILog _logger = LogManager.GetLogger(typeof(Adapter).ToString());

        public event EventHandler StreamCreated;

        private readonly ConcurrentDictionary<string, IListener> _listeners;
        private readonly List<string> _sports;
        private readonly BlockingCollection<IResourceFacade> _resourceCreationQueue;
        private readonly HashSet<string> _currentlyProcessedFixtures;
        private readonly CancellationTokenSource _creationQueueCancellationToken;
        private readonly Task[] _creationTasks;
        private readonly IStatsHandle _Stats;
        private Timer _trigger;

        public Adapter(ISettings settings, IServiceFacade udapiServiceFacade, IAdapterPlugin platformConnector)
        {

            Settings = settings;
            UDAPIService = udapiServiceFacade;
            PlatformConnector = platformConnector;
            EventState = ProcessState.EventState.Create(new FileStoreProvider(), settings);

            var statemanager = new StateManager(settings);
            StateManager = statemanager;

            if (settings.StatsEnabled)
                StatsManager.Configure();

            // we just need the initialisation
            new SuspensionManager(statemanager, PlatformConnector);

            platformConnector.Initialise();


            ThreadPool.SetMinThreads(500, 500);

            _resourceCreationQueue = new BlockingCollection<IResourceFacade>(new ConcurrentQueue<IResourceFacade>());
            _currentlyProcessedFixtures = new HashSet<string>();
            _listeners = new ConcurrentDictionary<string, IListener>();
            _sports = new List<string>();
            _creationQueueCancellationToken = new CancellationTokenSource();

            _creationTasks = new Task[settings.FixtureCreationConcurrency];

            _Stats = StatsManager.Instance["adapter.core"].GetHandle();
        }


        internal IEventState EventState { get; set; }

        internal IStateManager StateManager { get; set; }

        internal IAdapterPlugin PlatformConnector { get; private set; }

        internal ISettings Settings { get; private set; }

        internal IServiceFacade UDAPIService { get; private set; }

        /// <summary>
        /// Starts the adapter.
        /// This method returns immediately leaving to a background worker
        /// the task of getting the data and process it.
        /// 
        /// Throws an exception if it can't initialise itself.
        /// </summary>
        public void Start()
        {
            try
            {
                LogVersions();

                _logger.Info("Adapter is connecting to the UDAPI service...");
                UDAPIService.Connect();
                if (!UDAPIService.IsConnected)
                    return;

                _logger.Debug("Adapter connected to the UDAPI - initialising...");

                for (var i = 0; i < Settings.FixtureCreationConcurrency; i++)
                {
                    _creationTasks[i] = Task.Factory.StartNew(CreateFixture, _creationQueueCancellationToken.Token);
                }

                foreach (var sport in UDAPIService.GetSports())
                {
                    if (Settings.Sports == null || Array.IndexOf(Settings.Sports, sport.Name) > -1)
                    {
                        _sports.Add(sport.Name);
                    }
                }

                _trigger = new Timer(timerAutoEvent => TimerEvent(), null, 0, Settings.FixtureCheckerFrequency);

                _logger.InfoFormat("Adapter initialised");
                _Stats.SetValue(AdapterCoreKeys.ADAPTER_STARTED, "1");
            }
            catch (Exception ex)
            {
                _logger.Fatal("A fatal error has occurred and the Adapater cannot start. You can try a manual restart", ex);
                throw;
            }
        }

        /// <summary>
        /// Allows to stop the adapter.
        /// 
        /// Before returning, and if it is so configured,
        /// the adapter sends a "suspend" request to 
        /// all the currently registred fixtures.
        /// </summary>
        public void Stop()
        {
            _logger.InfoFormat("Adapter is stopping");

            try
            {
                if (_trigger != null)
                {
                    var wait_handler = new ManualResetEvent(false);
                    _trigger.Dispose(wait_handler);
                    wait_handler.WaitOne();
                    wait_handler.Dispose();
                    _trigger = null;

                    _creationQueueCancellationToken.Cancel(false);
                    Task.WaitAll(_creationTasks);

                    if (_listeners != null)
                    {
                        try
                        {
                            DisposeListeners();
                        }
                        catch (AggregateException ax)
                        {
                            foreach (var exception in ax.InnerExceptions)
                            {
                                _logger.Error("Error during listener disposing", exception);
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.Error("Error during listener disposing", e);
                        }

                        _listeners.Clear();
                    }

                    EventState.WriteToFile();

                    _resourceCreationQueue.Dispose();
                    _creationQueueCancellationToken.Dispose();
                }

                if (PlatformConnector != null)
                    PlatformConnector.Dispose();

                UDAPIService.Disconnect();
            }
            catch (Exception e)
            {
                _logger.Error("An error occured while disposing the adapter", e);
            }

            _Stats.SetValue(AdapterCoreKeys.ADAPTER_STARTED, "0");
            _logger.InfoFormat("Adapter stopped");
        }

        private void DisposeListeners()
        {
            _logger.Debug("Stopping listeners and suspending fixtures as service is shouting down");

            Parallel.ForEach(
                _listeners.Values,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                listener => listener.Dispose());
        }

        /// <summary>
        /// This method could be accessed by multi threads if the ProcessSport takes long time
        /// </summary>
        private void TimerEvent()
        {
            try
            {
                _logger.DebugFormat("Adapter is querying API for fixtures");

                var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

                Parallel.ForEach(_sports, parallelOptions, ProcessSport);

                GetStatistics();

            }
            catch (Exception ex)
            {
                if (ex is AggregateException)
                {
                    var ae = ex as AggregateException;
                    foreach (var exception in ae.InnerExceptions)
                    {
                        _logger.Error("Error processing sports: ", exception);
                    }
                }
                else
                {
                    _logger.Error("Error processing sports: ", ex);
                }

                ReconnectAPI();
            }
            finally
            {
                lock (_sync)
                    EventState.WriteToFile();
            }
        }

        private void ReconnectAPI()
        {
            var success = false;
            var attempts = 0;
            while (!success || attempts > Settings.MaxRetryAttempts)
            {
                try
                {
                    lock (_sync)
                    {
                        UDAPIService.Connect();
                        success = true;
                    }
                }
                catch (Exception ex)
                {
                    attempts++;
                    _logger.ErrorFormat("Error trying to create a new session: {0}", ex);
                }
            }
            if (!success)
            {
                _logger.Warn("Failed to create a session!");
            }
        }

        internal void AddSport(string sport)
        {
            if (!_sports.Contains(sport))
                _sports.Add(sport);
        }

        private void GetStatistics()
        {
            var queueSize = _resourceCreationQueue.Count;
            var currentlyConnected = _listeners.Count;

            try
            {
                _Stats.AddValueUnsafe(AdapterCoreKeys.ADAPTER_TOTAL_MEMORY, GC.GetTotalMemory(false).ToString());
                _Stats.AddValueUnsafe(AdapterCoreKeys.ADAPTER_RUNNING_THREADS, Process.GetCurrentProcess().Threads.Count.ToString());
                _Stats.SetValueUnsafe(AdapterCoreKeys.ADAPTER_HEALTH_CHECK, "1");
                _Stats.SetValueUnsafe(AdapterCoreKeys.ADAPTER_FIXTURE_TOTAL, currentlyConnected.ToString());

                foreach (var sport in _listeners.Values.GroupBy(x => x.Sport))
                {
                    var sportStatsHandle = StatsManager.Instance["adapter.core.fixture." + sport.Key].GetHandle();
                    sportStatsHandle.SetValueUnsafe(AdapterCoreKeys.SPORT_FIXTURE_TOTAL, sport.Count().ToString());
                    sportStatsHandle.SetValueUnsafe(AdapterCoreKeys.SPORT_FIXTURE_STREAMING_TOTAL, sport.Count(x => x.IsStreaming).ToString());
                    sportStatsHandle.SetValueUnsafe(AdapterCoreKeys.SPORT_FIXTURE_IN_PLAY_TOTAL, sport.Count(x => x.IsInPlay).ToString());
                }
            }
            catch { }

            _logger.DebugFormat("Currently adapter is streaming fixtureCount={0} and creation queue has queueSize={1} elements", currentlyConnected, queueSize);

        }

        internal void ProcessSport(string sport)
        {
            _logger.InfoFormat("Getting the list of available fixtures for sport={0} from GTP", sport);

            var resources = UDAPIService.GetResources(sport);

            if (resources == null)
            {
                _logger.WarnFormat("Cannot find sport={0} in UDAPI....", sport);
                return;
            }

            if (resources.Count == 0)
            {
                _logger.DebugFormat("There are currently no fixtures for sport={0} in UDAPI", sport);
                return;
            }

            var processingFactor = resources.Count / 10;

            _logger.DebugFormat("Received count={0} fixtures to process in sport={1}", resources.Count, sport);

            var po = new ParallelOptions { MaxDegreeOfParallelism = processingFactor == 0 ? 1 : processingFactor };

            if (resources.Count > 1)
            {
                resources.Sort((x, y) =>
                {
                    if (x.Content.MatchStatus > y.Content.MatchStatus)
                        return -1;

                    return x.Content.MatchStatus < y.Content.MatchStatus ? 1 : DateTime.Parse(x.Content.StartTime).CompareTo(DateTime.Parse(y.Content.StartTime));
                });
            }

            Parallel.ForEach(resources,
                po,
                resource =>
                {
                    try
                    {
                        ProcessResource(sport, resource);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(string.Format("An error occured while processing {0} for sport={1}", resource, sport), ex);
                    }
                });


            RemoveDeletedFixtures(sport, resources);

            _logger.InfoFormat("Finished processing fixtures for sport={0}", sport);
        }

        private void RemoveDeletedFixtures(string sport, IEnumerable<IResourceFacade> resources)
        {
            var currentfixturesLookup = resources.ToDictionary(r => r.Id);
            var allFixturesForSport = EventState.GetFixtures(sport);

            var deletedFixtures = allFixturesForSport.Where(fixtureId => !currentfixturesLookup.ContainsKey(fixtureId));

            foreach (var deletedFixtureId in deletedFixtures)
            {
                _logger.DebugFormat("Fixture with fixtureId={0} was deleted from Connect fixture factory", deletedFixtureId);
                RemoveAndStopListener(deletedFixtureId);
                EventState.RemoveFixture(sport, deletedFixtureId);
            }
        }

        private void ProcessResource(string sport, IResourceFacade resource)
        {
            _logger.DebugFormat("Attempt to process {0} for sport={1}", resource, sport);

            if (!CanProcessResource(resource))
                return;

            _logger.InfoFormat("Processing {0}", resource);

            if (_listeners.ContainsKey(resource.Id))
            {
                _logger.DebugFormat("Listener already exists for {0}", resource);

                IListener listener = _listeners[resource.Id];

                if (listener.IsFixtureDeleted)
                {
                    _logger.DebugFormat("{0} was deleted and republished. Listener wil be removed", resource);
                    RemoveAndStopListener(resource.Id);
                }
                else if (listener.IsIgnored)
                {
                    _logger.DebugFormat("{0} is marked as ignored. Listener wil be removed", resource);
                    RemoveAndStopListener(resource.Id);
                }
                else
                {
                    if (!StopListenerIfFixtureEnded(sport, resource))
                    {
                        _listeners[resource.Id].UpdateResourceState(resource);
                    }
                }

                MarkResourceAsProcessable(resource);
            }
            else
            {
                // Check fixture is not yet over, ignore if over
                var fixtureState = EventState.GetFixtureState(resource.Id);
                if (resource.IsMatchOver && (fixtureState == null || fixtureState.MatchStatus == resource.MatchStatus))
                {
                    _logger.InfoFormat("{0} is over. Adapter will not process the resource", resource);
                    MarkResourceAsProcessable(resource);
                    return;
                }


                _logger.DebugFormat("Adding {0} to the creation queue ", resource);
                _resourceCreationQueue.Add(resource);
                _logger.DebugFormat("Added {0} to the creation queue", resource);
            }

        }

        private void CreateFixture()
        {
            try
            {
                foreach (var resource in _resourceCreationQueue.GetConsumingEnumerable(_creationQueueCancellationToken.Token))
                {
                    try
                    {
                        _logger.DebugFormat("Task={0} is processing {1} from the queue", Task.CurrentId, resource);

                        if (_listeners.ContainsKey(resource.Id))
                            continue;

                        _logger.DebugFormat("Attempting to create a Listener for sport={0} and {1}", resource.Sport, resource);

                        var listener = new StreamListener(resource, PlatformConnector, EventState, StateManager);

                        if (!listener.Start())
                        {
                            _logger.WarnFormat("Couldn't start stream listener for {0}", resource);
                            continue;
                        }

                        _listeners.TryAdd(resource.Id, listener);

                        OnStreamCreated();

                        _logger.InfoFormat("Listener created for {0}", resource);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(string.Format("There has been a problem creating a listener for {0}", resource), ex);
                    }
                    finally
                    {
                        MarkResourceAsProcessable(resource);

                        _logger.DebugFormat("Finished processing fixture from queue {0}", resource);
                    }

                    if (_creationQueueCancellationToken.IsCancellationRequested)
                    {
                        _logger.DebugFormat("Fixture creation task={0} will terminate as requested", Task.CurrentId);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.DebugFormat("Fixture creation task={0} exited as requested", Task.CurrentId);
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("An error occured on fixture creation task={0}", Task.CurrentId), ex);
            }

        }

        private bool RemoveAndStopListener(string fixtureId)
        {
            _logger.InfoFormat("Removing listener for fixtureId={0}", fixtureId);

            IListener listener = null;
            _listeners.TryRemove(fixtureId, out listener);

            if (listener != null)
            {
                listener.Dispose();
            }

            return listener != null;
        }


        /// <summary>
        /// Stops and remove the listener if the fixture is over.
        /// Returns true if the fixture was over and the listener is removed.
        /// False otherwise
        /// </summary>
        /// <param name="sport"></param>
        /// <param name="resource"></param>
        /// <returns></returns>
        private bool StopListenerIfFixtureEnded(string sport, IResourceFacade resource)
        {
            var listener = _listeners[resource.Id];

            if (listener.IsFixtureEnded || resource.IsMatchOver)
            {
                _logger.DebugFormat("{0} is marked as ended - checking for stopping streaming", resource);

                FixtureState currState = EventState.GetFixtureState(resource.Id);

                if (currState != null && currState.MatchStatus != MatchStatus.MatchOver)
                {
                    _logger.DebugFormat("{0} is over but the MatchOver update has not been processed yet", resource);
                    return false;
                }

                _logger.InfoFormat("{0} is over. Listener will be removed", resource);

                if (RemoveAndStopListener(resource.Id))
                {
                    EventState.RemoveFixture(sport, resource.Id);
                }
                else
                {
                    _logger.WarnFormat("Couldn't remove listener for matchOver fixture {0}", resource);
                }

                return true;
            }

            return false;
        }

        private void LogVersions()
        {
            var executingAssembly = Assembly.GetExecutingAssembly();
            var version = executingAssembly.GetName().Version;
            var e = version.ToString();

            var sdkAssembly = Assembly.GetAssembly(typeof(ISession));
            var sdkVersion = sdkAssembly.GetName().Version;
            var s = sdkVersion.ToString();

            _logger.InfoFormat("Sporting Solutions Adapter version={0} using Sporting Solutions SDK version={1}", e, s);
        }

        private bool CanProcessResource(IResourceFacade resource)
        {
            // this prevents to take any decision
            // about the resource while it is
            // being processed by another thread
            lock (_sync)
            {
                if (_currentlyProcessedFixtures.Contains(resource.Id))
                {
                    _logger.DebugFormat("{0} is currently being processed by another task - ignoring it", resource);
                    return false;
                }

                _currentlyProcessedFixtures.Add(resource.Id);
            }

            return true;
        }

        private void MarkResourceAsProcessable(IResourceFacade resource)
        {
            lock (_sync)
            {
                if (_currentlyProcessedFixtures.Contains(resource.Id))
                    _currentlyProcessedFixtures.Remove(resource.Id);
            }
        }

        protected virtual void OnStreamCreated()
        {
            if (StreamCreated != null)
            {
                StreamCreated(this, EventArgs.Empty);
            }
        }
    }
}
