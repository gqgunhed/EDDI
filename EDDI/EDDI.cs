﻿using EliteDangerousCompanionAppService;
using EliteDangerousDataDefinitions;
using EliteDangerousDataProviderService;
using EliteDangerousEvents;
using EliteDangerousJournalMonitor;
using EliteDangerousNetLogMonitor;
using EliteDangerousStarMapService;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Utilities;

namespace EDDI
{
    // Notifications delegate
    public delegate void OnEventHandler(Event theEvent);

    /// <summary>
    /// Eddi is the controller for all EDDI operations.  Its job is to retain the state of the objects such as the commander, the current system, etc.
    /// and keep them up-to-date with changes that occur.  It also passes on messages to responders to handle as required.
    /// </summary>
    public class Eddi
    {
        public static readonly string EDDI_VERSION = "2.0.0b1";

        private static Eddi instance;

        private static readonly object instanceLock = new object();
        public static Eddi Instance
        {
            get
            {
                if (instance == null)
                {
                    Logging.Info("No EDDI instance: creating one");
                    lock (instanceLock)
                    {
                        if (instance == null)
                        {
                            Logging.Info("Definitely no EDDI instance: creating one");
                            instance = new Eddi();
                        }
                    }
                }
                return instance;
            }
        }

        public event OnEventHandler EventHandler;
        protected virtual void OnEvent(Event theEvent)
        {
            EventHandler?.Invoke(theEvent);
        }

        private List<EDDIResponder> responders = new List<EDDIResponder>();

        private CompanionAppService appService;

        // Information obtained from the companion app service
        public Commander Cmdr { get; private set; }
        public Ship Ship { get; private set; }
        public List<Ship> StoredShips { get; private set; }
        public Station LastStation { get; private set; }
        public List<Module> Outfitting { get; private set; }

        // Services made available from EDDI
        public StarMapService starMapService { get; private set; }
        public StarSystemRepository starSystemRepository { get; private set; }

        // Information obtained from the configuration
        public StarSystem HomeStarSystem { get; private set; }
        public Station HomeStation { get; private set; }
        public decimal? Insurance { get; private set; }

        // Information obtained from the log watcher
        public string Environment { get; private set; }
        public StarSystem CurrentStarSystem { get; private set; }
        public StarSystem LastStarSystem { get; private set; }

        private Thread logWatcherThread;

        public EDDIConfiguration configuration { get; private set; }

        public static readonly string ENVIRONMENT_SUPERCRUISE = "Supercruise";
        public static readonly string ENVIRONMENT_NORMAL_SPACE = "Normal space";

        private Eddi()
        {
            try
            {
                Logging.Info("EDDI " + EDDI_VERSION + " starting");

                // Set up and/or open our database
                String dataDir = System.Environment.GetEnvironmentVariable("AppData") + "\\EDDI";
                System.IO.Directory.CreateDirectory(dataDir);

                // Set up our local star system repository
                starSystemRepository = new StarSystemSqLiteRepository();

                // Set up the EDDI configuration
                configuration = EDDIConfiguration.FromFile();
                if (configuration.HomeSystem != null && configuration.HomeSystem.Trim().Length > 0)
                {
                    HomeStarSystem = starSystemRepository.GetOrCreateStarSystem(configuration.HomeSystem.Trim());

                    if (configuration.HomeStation != null && configuration.HomeStation.Trim().Length > 0)
                    {
                        string homeStationName = configuration.HomeStation.Trim();
                        foreach (Station station in HomeStarSystem.stations)
                        {
                            if (station.Name == homeStationName)
                            {
                                HomeStation = station;
                                break;
                            }
                        }
                    }
                }
                Logging.Verbose = configuration.Debug;
                Insurance = configuration.Insurance;

                // Set up the app service
                appService = new CompanionAppService();
                if (appService.CurrentState == CompanionAppService.State.READY)
                {
                    // Carry out initial population of profile
                    refreshProfile();
                }
                if (Cmdr != null && Cmdr.name != null)
                {
                    Logging.Info("EDDI access to the companion app is enabled");
                }
                else
                {
                    // If InvokeUpdatePlugin failed then it will have have left an error message, but this once we ignore it
                    Logging.Info("EDDI access to the companion app is disabled");
                }

                // Set up the star map service
                StarMapConfiguration starMapCredentials = StarMapConfiguration.FromFile();
                if (starMapCredentials != null && starMapCredentials.apiKey != null)
                {
                    // Commander name might come from star map credentials or the companion app's profile
                    string commanderName = null;
                    if (starMapCredentials.commanderName != null)
                    {
                        commanderName = starMapCredentials.commanderName;
                    }
                    else if (Cmdr.name != null)
                    {
                        commanderName = Cmdr.name;
                    }
                    if (commanderName != null)
                    {
                        starMapService = new StarMapService(starMapCredentials.apiKey, commanderName);
                        Logging.Info("EDDI access to EDSM is enabled");
                    }
                }
                if (starMapService == null)
                {
                    Logging.Info("EDDI access to EDSM is disabled");
                }

                // We always start in normal space
                Environment = ENVIRONMENT_NORMAL_SPACE;

                // Set up log monitor
                NetLogConfiguration netLogConfiguration = NetLogConfiguration.FromFile();
                if (netLogConfiguration != null && netLogConfiguration.path != null)
                {
                    logWatcherThread = new Thread(() => StartLogMonitor(netLogConfiguration));
                    logWatcherThread.IsBackground = true;
                    logWatcherThread.Name = "EDDI netlog watcher";
                    logWatcherThread.Start();
                    Logging.Info("EDDI netlog monitor is enabled for " + netLogConfiguration.path);
                }
                else
                {
                    Logging.Info("EDDI netlog monitor is disabled");
                }

                Logging.Info("EDDI " + EDDI_VERSION + " initialised");
            }
            catch (Exception ex)
            {
                Logging.Error("Failed to initialise: " + ex);
            }
        }

        private void StartLogMonitor(NetLogConfiguration configuration)
        {
            if (configuration != null)
            {
                JournalMonitor monitor = new JournalMonitor(configuration, (result) => journalEntryHandler(result));
                monitor.start();
            }
        }

        public void Start()
        {
            responders.Add(new SpeechResponder());
            responders.Add(new EDSMResponder());

            foreach (EDDIResponder responder in responders)
            {
                EventHandler += new OnEventHandler(responder.Handle);
                responder.Start();
            }
        }

        public void Stop()
        {
            foreach (EDDIResponder responder in responders)
            {
                responder.Stop();
            }
            if (logWatcherThread != null)
            {
                logWatcherThread.Abort();
                logWatcherThread = null;
            }

            Logging.Info("EDDI " + EDDI_VERSION + " shutting down");
        }

        void journalEntryHandler(Event journalEvent)
        {
            Logging.Debug("Handling event " + JsonConvert.SerializeObject(journalEvent));
            // We have some additional processing to do for a number of events
            if (journalEvent is JumpedEvent)
            {
                eventJumped((JumpedEvent)journalEvent);
            }
            else if (journalEvent is DockedEvent)
            {
                eventDocked((DockedEvent)journalEvent);
            }
            else if (journalEvent is UndockedEvent)
            {
                eventUndocked((UndockedEvent)journalEvent);
            }
            else if (journalEvent is EnteredSupercruiseEvent)
            {
                eventEnteredSupercruise((EnteredSupercruiseEvent)journalEvent);
            }
            else if (journalEvent is EnteredNormalSpaceEvent)
            {
                eventEnteredNormalSpace((EnteredNormalSpaceEvent)journalEvent);
            }
            // Additional processing is over, send to the event responders
            OnEvent(journalEvent);
        }

        void eventDocked(DockedEvent theEvent)
        {
        }

        void eventUndocked(UndockedEvent theEvent)
        {
        }

        void eventJumped(JumpedEvent theEvent)
        {
            if (CurrentStarSystem == null || CurrentStarSystem.name != theEvent.system)
            {
                LastStarSystem = CurrentStarSystem;
                CurrentStarSystem = starSystemRepository.GetOrCreateStarSystem(theEvent.system);
                if (CurrentStarSystem.x == null)
                {
                    // Star system is missing co-ordinates to take them from the event
                    CurrentStarSystem.x = theEvent.x;
                    CurrentStarSystem.y = theEvent.y;
                    CurrentStarSystem.z = theEvent.z;
                }
                CurrentStarSystem.visits++;
                CurrentStarSystem.lastvisit = DateTime.Now;
                starSystemRepository.SaveStarSystem(CurrentStarSystem);
                // After jump we are always in supercruise
                Environment = ENVIRONMENT_SUPERCRUISE;
                setCommanderTitle();
            }
        }

        void eventEnteredSupercruise(EnteredSupercruiseEvent theEvent)
        {
            Environment = ENVIRONMENT_SUPERCRUISE;
        }

        void eventEnteredNormalSpace(EnteredNormalSpaceEvent theEvent)
        {
            Environment = ENVIRONMENT_SUPERCRUISE;
        }

        /// <summary>Obtain information from the copmanion API and use it to refresh our own data</summary>
        private void refreshProfile()
        {
            if (appService != null)
            {
                Profile profile = appService.Profile();
                Cmdr = profile == null ? null : profile.Cmdr;
                Ship = profile == null ? null : profile.Ship;
                StoredShips = profile == null ? null : profile.StoredShips;
                CurrentStarSystem = profile == null ? null : profile.CurrentStarSystem;
                setCommanderTitle();
                // TODO last station string to station
                //LastStation = profile.LastStation;
                Outfitting = profile == null ? null : profile.Outfitting;
            }
        }

        /// <summary>Work out the title for the commander in the current system</summary>
        private static int minEmpireRatingForTitle = 3;
        private static int minFederationRatingForTitle = 1;
        private void setCommanderTitle()
        {
            if (Cmdr != null)
            {
                Cmdr.title = "Commander";
                if (CurrentStarSystem != null)
                {
                    if (CurrentStarSystem.allegiance == "Federation" && Cmdr.federationrating > minFederationRatingForTitle)
                    {
                        Cmdr.title = Cmdr.federationrank;
                    }
                    else if (CurrentStarSystem.allegiance == "Empire" && Cmdr.empirerating > minEmpireRatingForTitle)
                    {
                        Cmdr.title = Cmdr.empirerank;
                    }
                }
            }
        }
    }
}
