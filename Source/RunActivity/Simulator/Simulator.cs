﻿// COPYRIGHT 2009, 2010, 2011, 2012, 2013 by the Open Rails project.
// 
// This file is part of Open Rails.
// 
// Open Rails is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Open Rails is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Open Rails.  If not, see <http://www.gnu.org/licenses/>.

/// <summary>
/// This contains all the essential code to operate trains along paths as defined
/// in the activity.   It is meant to operate in a separate thread it handles the
/// following:
///    track paths
///    switch track positions
///    signal indications
///    calculating positions and velocities of trains
///    
/// Update is called regularly to
///     do physics calculations for train movement
///     compute new signal indications
///     operate ai trains
///     
/// All keyboard input comes from the viewer class as calls on simulator's methods.
/// </summary>

using Microsoft.Xna.Framework;
using MSTS.Formats;
using ORTS.Common;
using ORTS.Formats;
using ORTS.MultiPlayer;
using ORTS.Scripting;
using ORTS.Settings;
using ORTS.Viewer3D;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
#if ACTIVITY_EDITOR
using LibAE;
using LibAE.Formats;
using LibAE.Common;
#endif

namespace ORTS
{
    public class Simulator
    {
        public bool Paused = true;          // start off paused, set to true once the viewer is fully loaded and initialized
        public float GameSpeed = 1;
        /// <summary>
        /// Monotonically increasing time value (in seconds) for the simulation. Starts at 0 and only ever increases, at <see cref="GameSpeed"/>.
        /// Does not change if game is <see cref="Paused"/>.
        /// </summary>
        public double GameTime;
        /// <summary>
        /// "Time of day" clock value (in seconds) for the simulation. Starts at activity start time and may increase, at <see cref="GameSpeed"/>,
        /// or jump forwards or jump backwards.
        /// </summary>
        public double ClockTime;
        // while Simulator.Update() is running, objects are adjusted to this target time 
        // after Simulator.Update() is complete, the simulator state matches this time

        public readonly UserSettings Settings;

        public string BasePath;     // ie c:\program files\microsoft games\train simulator
        public string RoutePath;    // ie c:\program files\microsoft games\train simulator\routes\usa1  - may be different on different pc's

        // Primary Simulator Data 
        // These items represent the current state of the simulator 
        // In multiplayer games, these items must be kept in sync across all players
        // These items are what are saved and loaded in a game save.
        public string RoutePathName;    // ie LPS, USA1  represents the folder name
        public string RouteName;
        public string ActivityFileName;
        public string TimetableFileName;
        public bool TimetableMode;
        public ACTFile Activity;
        public Activity ActivityRun;
        public TDBFile TDB;
        public TRKFile TRK;
        public TRPFile TRP; // Track profile file
        public TSectionDatFile TSectionDat;
        public TrainList Trains;
        public Dictionary<int, Train> TrainDictionary = new Dictionary<int, Train>();
        public Dictionary<string, Train> NameDictionary = new Dictionary<string, Train>();
        public Dictionary<int, AITrain> AutoGenDictionary = new Dictionary<int, AITrain>();
        public List<int> StartReference = new List<int>();

        public Signals Signals;
        public AI AI;
        public RailDriverHandler RailDriver;
        public SeasonType Season;
        public WeatherType Weather;
        SIGCFGFile SIGCFG;
        public string ExplorePathFile;
        public string ExploreConFile;
        public string patFileName;
        public string conFileName;
        public AIPath PlayerPath;
        public LevelCrossings LevelCrossings;
        public RDBFile RDB;
        public CarSpawnerFile CarSpawnerFile;
        public bool UseAdvancedAdhesion;
        public bool BreakCouplers;
        public int DayAmbientLight;
        public int CarVibrating;
        public int CabRotating = 1;
        public int UseSuperElevation; //amount of superelevation
        public int SuperElevationMinLen = 50;
        public float SuperElevationGauge = 1.435f;//1.435 guage
        // Used in save and restore form
        public string PathName = "<unknown>";
        public float InitialTileX;
        public float InitialTileZ;
        public HazzardManager HazzardManager;
        public bool InControl = true;//For multiplayer, a player may not control his/her own train (as helper)
        /// <summary>
        /// Reference to the InterlockingSystem object, responsible for
        /// managing signalling and interlocking.
        /// </summary>

        public TrainCar PlayerLocomotive;    // Set by the Viewer - TODO there could be more than one player so eliminate this.

        // <CJComment> Works but not entirely happy about this arrangement. 
        // Confirmer should be part of the Viewer, rather than the Simulator, as it is part of the user interface.
        // Perhaps an Observer design pattern would be better, so the Simulator sends messages to any observers. </CJComment>
        public Confirmer Confirmer;                 // Set by the Viewer
        public Event SoundNotify = Event.None;
        public ScriptManager ScriptManager;
#if ACTIVITY_EDITOR
        public ORRouteConfig orRouteConfig;
#endif

        public Simulator(UserSettings settings, string activityPath, bool useOpenRailsDirectory)
        {
            TimetableMode = false;

            Settings = settings;
            UseAdvancedAdhesion = Settings.UseAdvancedAdhesion;
            BreakCouplers = Settings.BreakCouplers;
            CarVibrating = Settings.CarVibratingLevel; //0 no vib, 1-2 mid vib, 3 max vib
            UseSuperElevation = Settings.UseSuperElevation;
            SuperElevationMinLen = Settings.SuperElevationMinLen;
            SuperElevationGauge = (float)Settings.SuperElevationGauge / 1000f;//gauge transfer from mm to m
            RoutePath = Path.GetDirectoryName(Path.GetDirectoryName(activityPath));
            if (useOpenRailsDirectory) RoutePath = Path.GetDirectoryName(RoutePath); // starting one level deeper!
            RoutePathName = Path.GetFileName(RoutePath);
            BasePath = Path.GetDirectoryName(Path.GetDirectoryName(RoutePath));
            DayAmbientLight = (int)Settings.DayAmbientLight;

            string ORfilepath = System.IO.Path.Combine(RoutePath, "OpenRails");

            Trace.Write("Loading ");

            Trace.Write(" TRK");
            TRK = new TRKFile(MSTS.MSTSPath.GetTRKFileName(RoutePath));
            RouteName = TRK.Tr_RouteFile.Name;

            Trace.Write(" TDB");
            TDB = new TDBFile(RoutePath + @"\" + TRK.Tr_RouteFile.FileName + ".tdb");

            if (File.Exists(ORfilepath + @"\sigcfg.dat"))
            {
                Trace.Write(" SIGCFG_OR");
                SIGCFG = new SIGCFGFile(ORfilepath + @"\sigcfg.dat");
            }
            else
            {
                Trace.Write(" SIGCFG");
                SIGCFG = new SIGCFGFile(RoutePath + @"\sigcfg.dat");
            }

            Trace.Write(" DAT");
            if (Directory.Exists(RoutePath + @"\GLOBAL") && File.Exists(RoutePath + @"\GLOBAL\TSECTION.DAT"))
                TSectionDat = new TSectionDatFile(RoutePath + @"\GLOBAL\TSECTION.DAT");
            else
                TSectionDat = new TSectionDatFile(BasePath + @"\GLOBAL\TSECTION.DAT");
            if (File.Exists(RoutePath + @"\TSECTION.DAT"))
                TSectionDat.AddRouteTSectionDatFile(RoutePath + @"\TSECTION.DAT");

#if ACTIVITY_EDITOR
            //  Where we try to load OR's specific data description (Station, connectors, etc...)
            orRouteConfig = ORRouteConfig.LoadConfig(TRK.Tr_RouteFile.FileName, RoutePath, TypeEditor.NONE);
            orRouteConfig.SetTraveller(TSectionDat, TDB);
#endif

            RailDriver = new RailDriverHandler(BasePath);

            Trace.Write(" ACT");

            var rdbFile = RoutePath + @"\" + TRK.Tr_RouteFile.FileName + ".rdb";
            if (File.Exists(rdbFile))
            {
                Trace.Write(" RDB");
                RDB = new RDBFile(rdbFile);
            }

            var carSpawnFile = RoutePath + @"\carspawn.dat";
            if (File.Exists(carSpawnFile))
            {
                Trace.Write(" CARSPAWN");
                CarSpawnerFile = new CarSpawnerFile(RoutePath + @"\carspawn.dat", RoutePath + @"\shapes\");
            }

            HazzardManager = new HazzardManager(this);
            ScriptManager = new ScriptManager(this);

        }

        public void SetActivity(string activityPath)
        {
            ActivityFileName = Path.GetFileNameWithoutExtension(activityPath);
            Activity = new ACTFile(activityPath);
            ActivityRun = new Activity(Activity, this);
            if (ActivityRun.Current == null && ActivityRun.EventList.Count == 0)
                ActivityRun = null;

            StartTime st = Activity.Tr_Activity.Tr_Activity_Header.StartTime;
            TimeSpan StartTime = new TimeSpan(st.Hour, st.Minute, st.Second);
            ClockTime = StartTime.TotalSeconds;
            Season = Activity.Tr_Activity.Tr_Activity_Header.Season;
            Weather = Activity.Tr_Activity.Tr_Activity_Header.Weather;
            if (Activity.Tr_Activity.Tr_Activity_File.ActivityRestrictedSpeedZones != null)
            {
                ORTS.Activity.AddRestrictZones(TRK.Tr_RouteFile, TSectionDat, TDB.TrackDB, Activity.Tr_Activity.Tr_Activity_File.ActivityRestrictedSpeedZones);
            }
        }
        public void SetExplore(string path, string consist, string start, string season, string weather)
        {
            ExplorePathFile = path;
            ExploreConFile = consist;
            var time = start.Split(':');
            TimeSpan StartTime = new TimeSpan(int.Parse(time[0]), time.Length > 1 ? int.Parse(time[1]) : 0, time.Length > 2 ? int.Parse(time[2]) : 0);
            ClockTime = StartTime.TotalSeconds;
            Season = (SeasonType)int.Parse(season);
            Weather = (WeatherType)int.Parse(weather);
        }

        public void Start()
        {
            Signals = new Signals(this, SIGCFG);
            LevelCrossings = new LevelCrossings(this);
            Train playerTrain = InitializeTrains();
            AI = new AI(this, ClockTime);
            if (playerTrain != null)
            {
                playerTrain.PostInit();  // place player train after pre-running of AI trains
                TrainDictionary.Add(playerTrain.Number, playerTrain);
                NameDictionary.Add(playerTrain.Name, playerTrain);
            }
            MPManager.Instance().RememberOriginalSwitchState();

            // start activity logging if required
            if (Settings.DataLogStationStops && ActivityRun != null)
            {
                string stationLogFile = DeriveLogFile("Stops");
                if (!String.IsNullOrEmpty(stationLogFile))
                {
                    ActivityRun.StartStationLogging(stationLogFile);
                }
            }
        }

        public void StartTimetable(string[] arguments)
        {
            TimetableMode = true;
            Signals = new Signals(this, SIGCFG);
            LevelCrossings = new LevelCrossings(this);
            Trains = new TrainList(this);
            PathName = String.Copy(arguments[1]);

            TimetableInfo TTinfo = new TimetableInfo(this);

            Train playerTrain = null;
            List<AITrain> allTrains = TTinfo.ProcessTimetable(arguments, ref playerTrain);
            Trains[0] = playerTrain;

            AI = new AI(this, allTrains, ClockTime, playerTrain.FormedOf, playerTrain);

            Season = (SeasonType)int.Parse(arguments[3]);
            Weather = (WeatherType)int.Parse(arguments[4]);

            if (playerTrain != null)
            {
                playerTrain.PostInit();  // place player train after pre-running of AI trains
                playerTrain.SetupStationStopHandling(); // player train must now perform own station stop handling (no activity function available)
                TrainDictionary.Add(playerTrain.Number, playerTrain);
                NameDictionary.Add(playerTrain.Name, playerTrain);
            }

            //          MPManager.Instance().RememberOriginalSwitchState();

            // start activity logging if required
            if (Settings.DataLogStationStops && ActivityRun != null)
            {
                string stationLogFile = DeriveLogFile("Stops");
                if (!String.IsNullOrEmpty(stationLogFile))
                {
                    ActivityRun.StartStationLogging(stationLogFile);
                }
            }
        }

        public void Stop()
        {
            if (RailDriver != null)
                RailDriver.Shutdown();
            if (MPManager.IsMultiPlayer()) MPManager.Stop();
        }

        public void Restore(BinaryReader inf, float initialTileX, float initialTileZ)
        {
            ClockTime = inf.ReadDouble();
            Season = (SeasonType)inf.ReadInt32();
            Weather = (WeatherType)inf.ReadInt32();
            TimetableMode = inf.ReadBoolean();
            InitialTileX = initialTileX;
            InitialTileZ = initialTileZ;

            Signals = new Signals(this, SIGCFG, inf);
            RestoreTrains(inf);
            LevelCrossings = new LevelCrossings(this);
            AI = new AI(this, inf);
            ActivityRun = ORTS.Activity.Restore(inf, this, ActivityRun);
            Signals.RestoreTrains(Trains);  // restore links to trains
        }

        public void Save(BinaryWriter outf)
        {
            outf.Write(ClockTime);
            outf.Write((int)Season);
            outf.Write((int)Weather);
            outf.Write(TimetableMode);
            Signals.Save(outf);
            SaveTrains(outf);
            // LevelCrossings
            // InterlockingSystem
            AI.Save(outf);

            ORTS.Activity.Save(outf, ActivityRun);
        }

        Train InitializeTrains()
        {
            Trains = new TrainList(this);
            Train playerTrain = InitializePlayerTrain();
            InitializeStaticConsists();
            return (playerTrain);
        }

        /// <summary>
        /// Which locomotive does the activity specify for the player.
        /// </summary>
        public TrainCar InitialPlayerLocomotive()
        {
            Train playerTrain = Trains[0];    // we install the player train first
            TrainCar PlayerLocomotive = null;
            foreach (TrainCar car in playerTrain.Cars)
                if (car.IsDriveable)  // first loco is the one the player drives
                {
                    PlayerLocomotive = car;
                    playerTrain.LeadLocomotive = car;
 /*                   if (playerTrain.InitialSpeed == 0 || 
                       (PlayerLocomotive is MSTSSteamLocomotive) || ((PlayerLocomotive is MSTSDieselLocomotive) && //TODO: extend to other types of locos
                    ((MSTSDieselLocomotive)PlayerLocomotive).GearBox != null || ((MSTSDieselLocomotive)PlayerLocomotive).GearBoxController != null) ||
                        ! (PlayerLocomotive.BrakeSystem is AirSinglePipe)) */
                    playerTrain.InitializeBrakes();
                    PlayerLocomotive.LocalThrottlePercent = playerTrain.AITrainThrottlePercent;
                    break;
                }
            if (PlayerLocomotive == null)
                throw new InvalidDataException("Can't find player locomotive in activity");
            return PlayerLocomotive;
        }


        /// <summary>
        /// Convert and elapsed real time into clock time based on simulator
        /// running speed and paused state.
        /// </summary>
        public float GetElapsedClockSeconds(float elapsedRealSeconds)
        {
            return elapsedRealSeconds * (Paused ? 0 : GameSpeed);
        }

        /// <summary>
        /// Update the simulator state 
        /// elapsedClockSeconds represents the time since the last call to Simulator.Update
        /// Executes in the UpdaterProcess thread.
        /// </summary>
        [CallOnThread("Updater")]
        public void Update(float elapsedClockSeconds)
        {
            // Advance the times.
            GameTime += elapsedClockSeconds;
            ClockTime += elapsedClockSeconds;

            // Represent conditions at the specified clock time.
            List<Train> movingTrains = new List<Train>();

            if (PlayerLocomotive != null)
            {
                movingTrains.Add(PlayerLocomotive.Train);
                if (String.Compare(PlayerLocomotive.Train.LeadLocomotive.CarID, PlayerLocomotive.CarID) != 0)
                {
                    if (!MPManager.IsMultiPlayer()) PlayerLocomotive = PlayerLocomotive.Train.LeadLocomotive; //in MP, will not change player locomotive, By JTang
                }
            }

            foreach (Train train in Trains)
            {
                if (train.SpeedMpS != 0 &&
                    train.GetType() != typeof(AITrain) &&
                    (PlayerLocomotive == null || train != PlayerLocomotive.Train))
                {
                    movingTrains.Add(train);
                }
            }

            foreach (Train train in movingTrains)
            {
                if (MPManager.IsMultiPlayer())
                {
                    try
                    {
                        train.Update(elapsedClockSeconds);
                    }
                    catch (Exception e) { Trace.TraceWarning(e.Message); }
                }
                else
                {
                    train.Update(elapsedClockSeconds);
                }
            }

            foreach (Train train in movingTrains)
            {
                CheckForCoupling(train, elapsedClockSeconds);
            }

            if (Signals != null)
            {
                if (!MPManager.IsMultiPlayer() || MPManager.IsServer()) Signals.Update(false);
            }

            if (AI != null)
            {
                AI.Update(elapsedClockSeconds);
            }

            LevelCrossings.Update(elapsedClockSeconds);

            if (ActivityRun != null)
            {
                ActivityRun.Update();
            }

            if (RailDriver != null)
            {
                RailDriver.Update(PlayerLocomotive);
            }

            if (HazzardManager != null) HazzardManager.Update(elapsedClockSeconds);
            if (MPManager.IsMultiPlayer()) MPManager.Instance().Update(GameTime);

        }

        private void FinishFrontCoupling(Train drivenTrain, Train train, TrainCar lead)
        {
            drivenTrain.LeadLocomotive = lead;
            drivenTrain.CalculatePositionOfCars(0);
            FinishCoupling(drivenTrain, train, true);
        }

        private void FinishRearCoupling(Train drivenTrain, Train train)
        {
            drivenTrain.RepositionRearTraveller();
            FinishCoupling(drivenTrain, train, false);
        }

        private void FinishCoupling(Train drivenTrain, Train train, bool couple_to_front)
        {
            train.RemoveFromTrack();
            Trains.Remove(train);
            TrainDictionary.Remove(train.Number);
            NameDictionary.Remove(train.Name.ToLower());

            if (train.UncoupledFrom != null)
                train.UncoupledFrom.UncoupledFrom = null;

            if (PlayerLocomotive != null && PlayerLocomotive.Train == train)
            {
                drivenTrain.AITrainThrottlePercent = train.AITrainThrottlePercent;
                drivenTrain.AITrainBrakePercent = train.AITrainBrakePercent;
                drivenTrain.LeadLocomotive = PlayerLocomotive;
            }

            drivenTrain.UpdateTrackActionsCoupling(couple_to_front);
        }

        static void UpdateUncoupled(Train drivenTrain, Train train, float d1, float d2, bool rear)
        {
            if (train == drivenTrain.UncoupledFrom && d1 > .5 && d2 > .5)
            {
                Traveller traveller = rear ? drivenTrain.RearTDBTraveller : drivenTrain.FrontTDBTraveller;
                float d3 = traveller.OverlapDistanceM(train.FrontTDBTraveller, rear);
                float d4 = traveller.OverlapDistanceM(train.RearTDBTraveller, rear);
                if (d3 > .5 && d4 > .5)
                {
                    train.UncoupledFrom = null;
                    drivenTrain.UncoupledFrom = null;
                }
            }
        }

        /// <summary>
        /// Scan other trains
        /// </summary>
        public void CheckForCoupling(Train drivenTrain, float elapsedClockSeconds)
        {
            if (MPManager.IsMultiPlayer() && !MPManager.IsServer()) return; //in MultiPlayer mode, server will check coupling, client will get message and do things
            if (drivenTrain.SpeedMpS < 0)
            {
                foreach (Train train in Trains)
                    if (train != drivenTrain)
                    {
                        //avoid coupling of player train with other players train
                        if (MPManager.IsMultiPlayer() && !MPManager.TrainOK2Couple(drivenTrain, train)) continue;

                        float d1 = drivenTrain.RearTDBTraveller.OverlapDistanceM(train.FrontTDBTraveller, true);
                        if (d1 < 0)
                        {
                            if (train == drivenTrain.UncoupledFrom)
                            {
                                if (drivenTrain.SpeedMpS < train.SpeedMpS)
                                    drivenTrain.SetCoupleSpeed(train, 1);
                                drivenTrain.CalculatePositionOfCars(-d1);
                                return;
                            }
                            // couple my rear to front of train
                            //drivenTrain.SetCoupleSpeed(train, 1);
                            drivenTrain.LastCar.SignalEvent(Event.Couple);
                            foreach (TrainCar car in train.Cars)
                            {
                                drivenTrain.Cars.Add(car);
                                car.Train = drivenTrain;
                                car.prevElev = -60f;
                            }
                            FinishRearCoupling(drivenTrain, train);
                            if (MPManager.IsMultiPlayer()) MPManager.BroadCast((new MSGCouple(drivenTrain, train)).ToString());
                            return;
                        }
                        float d2 = drivenTrain.RearTDBTraveller.OverlapDistanceM(train.RearTDBTraveller, true);
                        if (d2 < 0)
                        {
                            if (train == drivenTrain.UncoupledFrom)
                            {
                                if (drivenTrain.SpeedMpS < -train.SpeedMpS)
                                    drivenTrain.SetCoupleSpeed(train, 11);
                                drivenTrain.CalculatePositionOfCars(-d2);
                                return;
                            }
                            // couple my rear to rear of train
                            //drivenTrain.SetCoupleSpeed(train, -1);
                            drivenTrain.LastCar.SignalEvent(Event.Couple);
                            for (int i = train.Cars.Count - 1; i >= 0; --i)
                            {
                                TrainCar car = train.Cars[i];
                                drivenTrain.Cars.Add(car);
                                car.Train = drivenTrain;
                                car.Flipped = !car.Flipped;
                                car.prevElev = -60f;
                            }
                            FinishRearCoupling(drivenTrain, train);
                            if (MPManager.IsMultiPlayer()) MPManager.BroadCast((new MSGCouple(drivenTrain, train)).ToString());
                            return;
                        }
                        UpdateUncoupled(drivenTrain, train, d1, d2, false);
                    }
            }
            else if (drivenTrain.SpeedMpS > 0)
            {
                foreach (Train train in Trains)
                    if (train != drivenTrain)
                    {
                        //avoid coupling of player train with other players train if it is too short alived (e.g, when a train is just spawned, it may overlap with another train)
                        if (MPManager.IsMultiPlayer() && !MPManager.TrainOK2Couple(drivenTrain, train)) continue;
                        //	{
                        //		if ((MPManager.Instance().FindPlayerTrain(train) && drivenTrain == PlayerLocomotive.Train) || (MPManager.Instance().FindPlayerTrain(drivenTrain) && train == PlayerLocomotive.Train)) continue;
                        //		if ((MPManager.Instance().FindPlayerTrain(train) && MPManager.Instance().FindPlayerTrain(drivenTrain))) continue; //if both are player-controlled trains
                        //	}
                        float d1 = drivenTrain.FrontTDBTraveller.OverlapDistanceM(train.RearTDBTraveller, false);
                        if (d1 < 0)
                        {
                            if (train == drivenTrain.UncoupledFrom)
                            {
                                if (drivenTrain.SpeedMpS > train.SpeedMpS)
                                    drivenTrain.SetCoupleSpeed(train, 1);
                                drivenTrain.CalculatePositionOfCars(d1);
                                return;
                            }
                            // couple my front to rear of train
                            //drivenTrain.SetCoupleSpeed(train, 1);
                            drivenTrain.FirstCar.SignalEvent(Event.Couple);
                            TrainCar lead = drivenTrain.LeadLocomotive;
                            for (int i = 0; i < train.Cars.Count; ++i)
                            {
                                TrainCar car = train.Cars[i];
                                drivenTrain.Cars.Insert(i, car);
                                car.Train = drivenTrain;
                                car.prevElev = -60f;
                            }
                            FinishFrontCoupling(drivenTrain, train, lead);
                            if (MPManager.IsMultiPlayer()) MPManager.BroadCast((new MSGCouple(drivenTrain, train)).ToString());
                            return;
                        }
                        float d2 = drivenTrain.FrontTDBTraveller.OverlapDistanceM(train.FrontTDBTraveller, false);
                        if (d2 < 0)
                        {
                            if (train == drivenTrain.UncoupledFrom)
                            {
                                if (drivenTrain.SpeedMpS > -train.SpeedMpS)
                                    drivenTrain.SetCoupleSpeed(train, -1);
                                drivenTrain.CalculatePositionOfCars(d2);
                                return;
                            }
                            // couple my front to front of train
                            //drivenTrain.SetCoupleSpeed(train, -1);
                            drivenTrain.FirstCar.SignalEvent(Event.Couple);
                            TrainCar lead = drivenTrain.LeadLocomotive;
                            for (int i = 0; i < train.Cars.Count; ++i)
                            {
                                TrainCar car = train.Cars[i];
                                drivenTrain.Cars.Insert(0, car);
                                car.Train = drivenTrain;
                                car.Flipped = !car.Flipped;
                                car.prevElev = -60f;
                            }
                            FinishFrontCoupling(drivenTrain, train, lead);
                            if (MPManager.IsMultiPlayer()) MPManager.BroadCast((new MSGCouple(drivenTrain, train)).ToString());
                            return;
                        }

                        UpdateUncoupled(drivenTrain, train, d1, d2, true);
                    }
            }
        }

        private Train InitializePlayerTrain()
        {

            Debug.Assert(Trains != null, "Cannot InitializePlayerTrain() without Simulator.Trains.");
            // set up the player locomotive
            // first extract the player service definition from the activity file
            // this gives the consist and path

            Train train = new Train(this);
            train.TrainType = Train.TRAINTYPE.PLAYER;
            train.Number = 0;
            train.Name = "PLAYER";

            if (Activity == null)
            {
                patFileName = ExplorePathFile;
                conFileName = ExploreConFile;
            }
            else
            {
                string playerServiceFileName = Activity.Tr_Activity.Tr_Activity_File.Player_Service_Definition.Name;
                SRVFile srvFile = new SRVFile(RoutePath + @"\SERVICES\" + playerServiceFileName + ".SRV");
                train.InitialSpeed = srvFile.TimeTable.InitialSpeed;
                conFileName = BasePath + @"\TRAINS\CONSISTS\" + srvFile.Train_Config + ".CON";
                patFileName = RoutePath + @"\PATHS\" + srvFile.PathID + ".PAT";
            }


            if (conFileName.Contains("tilted")) train.tilted = true;


            //PATFile patFile = new PATFile(patFileName);
            //PathName = patFile.Name;
            // This is the position of the back end of the train in the database.
            //PATTraveller patTraveller = new PATTraveller(patFileName);
#if ACTIVITY_EDITOR
            AIPath aiPath = new AIPath(TDB, TSectionDat, patFileName, orRouteConfig);
#else
            AIPath aiPath = new AIPath(TDB, TSectionDat, patFileName);
#endif
            PathName = aiPath.pathName;

            if (aiPath.Nodes == null)
            {
                throw new InvalidDataException("Broken path " + patFileName + " for Player train - activity cannot be started");
            }

            // place rear of train on starting location of aiPath.
            train.RearTDBTraveller = new Traveller(TSectionDat, TDB.TrackDB.TrackNodes, aiPath);

            CONFile conFile = new CONFile(conFileName);

            // add wagons
            foreach (Wagon wagon in conFile.Train.TrainCfg.WagonList)
            {

                string wagonFolder = BasePath + @"\trains\trainset\" + wagon.Folder;
                string wagonFilePath = wagonFolder + @"\" + wagon.Name + ".wag"; ;
                if (wagon.IsEngine)
                    wagonFilePath = Path.ChangeExtension(wagonFilePath, ".eng");

                if (!File.Exists(wagonFilePath))
                {
                    // First wagon is the player's loco and required, so issue a fatal error message
                    if (wagon == conFile.Train.TrainCfg.WagonList[0])
                        Trace.TraceError("Player's locomotive {0} cannot be loaded in {1}", wagonFilePath, conFileName);
                    Trace.TraceWarning("Ignored missing wagon {0} in consist {1}", wagonFilePath, conFileName);
                    continue;
                }

                try
                {
                    TrainCar car = RollingStock.Load(this, wagonFilePath);
                    car.Flipped = wagon.Flip;
                    car.UiD = wagon.UiD;
                    if (MPManager.IsMultiPlayer()) car.CarID = MPManager.GetUserName() + " - " + car.UiD; //player's train is always named train 0.
                    else car.CarID = "0 - " + car.UiD; //player's train is always named train 0.
                    train.Cars.Add(car);
                    car.Train = train;
                    train.Length += car.LengthM;

                    var mstsDieselLocomotive = car as MSTSDieselLocomotive;
                    if (Activity != null && mstsDieselLocomotive != null)
                        mstsDieselLocomotive.DieselLevelL = mstsDieselLocomotive.MaxDieselLevelL * Activity.Tr_Activity.Tr_Activity_Header.FuelDiesel / 100.0f;
                }
                catch (Exception error)
                {
                    // First wagon is the player's loco and required, so issue a fatal error message
                    if (wagon == conFile.Train.TrainCfg.WagonList[0])
                        throw new FileLoadException(wagonFilePath, error);
                    Trace.WriteLine(new FileLoadException(wagonFilePath, error));
                }
            }// for each rail car

            train.CheckFreight();

            if (Activity != null && !MPManager.IsMultiPlayer()) // activity is defined
            {
                // define style of passing path and process player passing paths as required
                if (Settings.UseLocationPassingPaths)
                {
                    Signals.UseLocationPassingPaths = true;
                    int orgDirection = (train.RearTDBTraveller != null) ? (int)train.RearTDBTraveller.Direction : -2;
                    Train.TCRoutePath dummyRoute = new Train.TCRoutePath(aiPath, orgDirection, 0, Signals, -1, Settings);   // SPA: Add settings to get enhanced mode
                }
                else
                {
                    Signals.UseLocationPassingPaths = false;
                }

                // create train path
                train.SetRoutePath(aiPath, Signals);
                train.BuildWaitingPointList(0.0f);

                train.ConvertPlayerTraffic(Activity.Tr_Activity.Tr_Activity_File.Player_Service_Definition.Player_Traffic_Definition.Player_Traffic_List);
            }
            else // explorer mode
            {
                train.PresetExplorerPath(aiPath, Signals);
                train.ControlMode = Train.TRAIN_CONTROL.EXPLORER;
            }

            bool canPlace = true;
            Train.TCSubpathRoute tempRoute = train.CalculateInitialTrainPosition(ref canPlace);
            if (tempRoute.Count == 0 || !canPlace)
            {
                throw new InvalidDataException("Player train original position not clear");
            }

            train.SetInitialTrainRoute(tempRoute);
            train.CalculatePositionOfCars(0);
            train.ResetInitialTrainRoute(tempRoute);

            train.CalculatePositionOfCars(0);
            Trains.Add(train);
            if ((conFile.Train.TrainCfg.MaxVelocity != null) && (conFile.Train.TrainCfg.MaxVelocity.A <= 0f))
                train.TrainMaxSpeedMpS = (float)TRK.Tr_RouteFile.SpeedLimit;
            else
                train.TrainMaxSpeedMpS = Math.Min((float)TRK.Tr_RouteFile.SpeedLimit, conFile.Train.TrainCfg.MaxVelocity.A);
            // Note the initial position to be stored by a Save and used in Menu.exe to calculate DistanceFromStartM 
            InitialTileX = Trains[0].FrontTDBTraveller.TileX + (Trains[0].FrontTDBTraveller.X / 2048);
            InitialTileZ = Trains[0].FrontTDBTraveller.TileZ + (Trains[0].FrontTDBTraveller.Z / 2048);

            PlayerLocomotive = InitialPlayerLocomotive ();

            if (Activity != null && train.InitialSpeed > 0)
            {
                if (((PlayerLocomotive is MSTSElectricLocomotive) || (PlayerLocomotive is MSTSDieselLocomotive)) &&
                    (PlayerLocomotive.BrakeSystem is AirSinglePipe))
                    train.InitializeMoving();
             }
            else train.AITrainBrakePercent = 100;

            return (train);
        }

        /// <summary>
        /// Set up trains based on info in the static consists listed in the activity file.
        /// </summary>
        private void InitializeStaticConsists()
        {
            if (Activity == null) return;
            if (Activity.Tr_Activity == null) return;
            if (Activity.Tr_Activity.Tr_Activity_File == null) return;
            if (Activity.Tr_Activity.Tr_Activity_File.ActivityObjects == null) return;
            if (Activity.Tr_Activity.Tr_Activity_File.ActivityObjects.ActivityObjectList == null) return;
            // for each static consist
            foreach (ActivityObject activityObject in Activity.Tr_Activity.Tr_Activity_File.ActivityObjects.ActivityObjectList)
            {
                try
                {
                    // construct train data
                    Train train = new Train(this);
                    train.TrainType = Train.TRAINTYPE.STATIC;
                    train.Name = "STATIC";
                    int consistDirection;
                    switch (activityObject.Direction)  // TODO, we don't really understand this
                    {
                        case 0: consistDirection = 0; break;  // reversed ( confirmed on L&PS route )
                        case 18: consistDirection = 1; break;  // forward ( confirmed on ON route )
                        case 131: consistDirection = 1; break; // forward ( confirmed on L&PS route )
                        default: consistDirection = 1; break;  // forward ( confirmed on L&PS route )
                    }
                    // FIXME: Where are TSectionDat and TDB from?
                    train.RearTDBTraveller = new Traveller(TSectionDat, TDB.TrackDB.TrackNodes, activityObject.TileX, activityObject.TileZ, activityObject.X, activityObject.Z);
                    if (consistDirection != 1)
                        train.RearTDBTraveller.ReverseDirection();
                    // add wagons in reverse order - ie first wagon is at back of train
                    // static consists are listed back to front in the activities, so we have to reverse the order, and flip the cars
                    // when we add them to ORTS
                    for (int iWagon = activityObject.Train_Config.TrainCfg.WagonList.Count - 1; iWagon >= 0; --iWagon)
                    {
                        Wagon wagon = (Wagon)activityObject.Train_Config.TrainCfg.WagonList[iWagon];
                        string wagonFolder = BasePath + @"\trains\trainset\" + wagon.Folder;
                        string wagonFilePath = wagonFolder + @"\" + wagon.Name + ".wag"; ;
                        if (wagon.IsEngine)
                            wagonFilePath = Path.ChangeExtension(wagonFilePath, ".eng");

                        if (!File.Exists(wagonFilePath))
                        {
                            Trace.TraceWarning("Ignored missing wagon {0} in activity definition {1}", wagonFilePath, activityObject.Train_Config.TrainCfg.Name);
                            continue;
                        }

                        try // Load could fail if file has bad data.
                        {
                            TrainCar car = RollingStock.Load(this, wagonFilePath);
                            car.Flipped = !wagon.Flip;
                            car.UiD = wagon.UiD;
                            car.CarID = activityObject.ID + " - " + car.UiD;
                            train.Cars.Add(car);
                            car.Train = train;
                        }
                        catch (Exception error)
                        {
                            Trace.WriteLine(new FileLoadException(wagonFilePath, error));
                        }
                    }// for each rail car

                    if (train.Cars.Count == 0) return;

                    // in static consists, the specified location represents the middle of the last car, 
                    // our TDB traveller is always at the back of the last car so it needs to be repositioned
                    TrainCar lastCar = train.LastCar;
                    train.RearTDBTraveller.ReverseDirection();
                    train.RearTDBTraveller.Move(lastCar.LengthM / 2f);
                    train.RearTDBTraveller.ReverseDirection();

                    train.CalculatePositionOfCars(0);
                    train.InitializeBrakes();

                    bool validPosition = train.PostInit();
                    if (validPosition)
                        Trains.Add(train);
                }
                catch (Exception error)
                {
                    Trace.WriteLine(error);
                }
            }// for each train
        }

        private void SaveTrains(BinaryWriter outf)
        {
            if (PlayerLocomotive.Train != Trains[0])
            {
                for (int i = 1; i < Trains.Count; i++)
                {
                    if (PlayerLocomotive.Train == Trains[i])
                    {
                        Trains[i] = Trains[0];
                        Trains[0] = PlayerLocomotive.Train;
                        break;
                    }
                }
            }

            // do not save AI trains (done by AITrain)

            foreach (Train train in Trains)
            {
                if (train.TrainType != Train.TRAINTYPE.AI)
                {
                    outf.Write(0);
                    train.Save(outf);
                }
            }
            outf.Write(-1);

        }

        //================================================================================================//
        //
        // Restore trains
        //

        private void RestoreTrains(BinaryReader inf)
        {

            Trains = new TrainList(this);

            int trainType = inf.ReadInt32();
            while (trainType >= 0)
            {
                Trains.Add(new Train(this, inf));
                trainType = inf.ReadInt32();
            }

            // find player train
            foreach (Train thisTrain in Trains)
            {
                if (thisTrain.TrainType == Train.TRAINTYPE.PLAYER)
                {
                    TrainDictionary.Add(thisTrain.Number, thisTrain);
                    NameDictionary.Add(thisTrain.Name, thisTrain);
                    // restore signal references depending on state
                    if (thisTrain.ControlMode == Train.TRAIN_CONTROL.EXPLORER)
                    {
                        thisTrain.RestoreExplorerMode();
                    }
                    else if (thisTrain.ControlMode == Train.TRAIN_CONTROL.MANUAL)
                    {
                        thisTrain.RestoreManualMode();
                    }
                    else
                    {
                        thisTrain.InitializeSignals(true);
                    }
                }
            }
        }

        /// <summary>
        ///  Get Autogenerated train by number
        /// </summary>
        /// <param name="reqNumber"></param>
        /// <returns></returns>

        public AITrain GetAutoGenTrainByNumber(int reqNumber)
        {
            AITrain returnTrain = null;
            if (AutoGenDictionary.ContainsKey(reqNumber))
            {
                AITrain tempTrain = AutoGenDictionary[reqNumber];
                returnTrain = tempTrain.AICopyTrain();
                returnTrain.AI.AutoGenTrains.Remove(tempTrain);
                AutoGenDictionary.Remove(reqNumber);
                returnTrain.routedBackward = new Train.TrainRouted(returnTrain, 1);
                returnTrain.routedForward = new Train.TrainRouted(returnTrain, 0);
            }
            return (returnTrain);
        }

        /// <summary>
        /// The front end of a railcar is at MSTS world coordinates x1,y1,z1
        /// The other end is at x2,y2,z2
        /// Return a rotation and translation matrix for the center of the railcar.
        /// </summary>
        public static Matrix XNAMatrixFromMSTSCoordinates(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            // translate 1st coordinate to be relative to 0,0,0
            float dx = (float)(x1 - x2);
            float dy = (float)(y1 - y2);
            float dz = (float)(z1 - z2);

            // compute the rotational matrix  
            float length = (float)Math.Sqrt(dx * dx + dz * dz + dy * dy);
            float run = (float)Math.Sqrt(dx * dx + dz * dz);
            // normalize to coordinate to a length of one, ie dx is change in x for a run of 1
            dx /= length;
            dy /= length;   // ie if it is tilted back 5 degrees, this is sin 5 = 0.087
            run /= length;  //                              and   this is cos 5 = 0.996
            dz /= length;
            // setup matrix values

            Matrix xnaTilt = new Matrix(1, 0, 0, 0,
                                     0, run, dy, 0,
                                     0, -dy, run, 0,
                                     0, 0, 0, 1);

            Matrix xnaRotation = new Matrix(dz, 0, dx, 0,
                                            0, 1, 0, 0,
                                            -dx, 0, dz, 0,
                                            0, 0, 0, 1);

            Matrix xnaLocation = Matrix.CreateTranslation((x1 + x2) / 2f, (y1 + y2) / 2f, -(z1 + z2) / 2f);
            return xnaTilt * xnaRotation * xnaLocation;
        }

        public void UncoupleBehind(int carPosition)
        {
            // check on car position in case of mouse jitter
            if (carPosition <= PlayerLocomotive.Train.Cars.Count - 1) UncoupleBehind(PlayerLocomotive.Train.Cars[carPosition]);
        }

        public void UncoupleBehind(TrainCar car)
        {
            Train train = car.Train;

            if (MPManager.IsMultiPlayer() && !MPManager.TrainOK2Decouple(train)) return;
            int i = 0;
            while (train.Cars[i] != car) ++i;  // it can't happen that car isn't in car.Train
            if (i == train.Cars.Count - 1) return;  // can't uncouple behind last car
            ++i;

            TrainCar lead = train.LeadLocomotive;
            // move rest of cars to the new train
            Train train2 = new Train(this, train);
            if (MPManager.IsMultiPlayer()) train2.ControlMode = Train.TRAIN_CONTROL.EXPLORER;

            for (int k = i; k < train.Cars.Count; ++k)
            {
                TrainCar newcar = train.Cars[k];
                train2.Cars.Add(newcar);
                newcar.Train = train2;
            }

            // and drop them from the old train
            for (int k = train.Cars.Count - 1; k >= i; --k)
            {
                train.Cars.RemoveAt(k);
            }

            train.LastCar.CouplerSlackM = 0;

            // ensure player train keeps the original number (in single mode, it is always no. 0)
            if (PlayerLocomotive != null && PlayerLocomotive.Train == train2)
            {
                var temp = train.Number;
                train.Number = train2.Number;    // train gets new number
                train2.Number = temp;               // player train keeps the original number
            }

            // and fix up the travellers
            train2.RearTDBTraveller = new Traveller(train.RearTDBTraveller);
            train2.CalculatePositionOfCars(0);  // fix the front traveller
            train.RepositionRearTraveller();    // fix the rear traveller

            Trains.Add(train2);

            train.UncoupledFrom = train2;
            train2.UncoupledFrom = train;
            train2.SpeedMpS = train.SpeedMpS;
            train2.AITrainBrakePercent = train.AITrainBrakePercent;
            train2.AITrainDirectionForward = train.AITrainDirectionForward;
            if (PlayerLocomotive != null && PlayerLocomotive.Train == train2)
            {
                train2.AITrainThrottlePercent = train.AITrainThrottlePercent;
                train.AITrainThrottlePercent = 0;
                train2.TrainType = Train.TRAINTYPE.PLAYER;
                train.TrainType = Train.TRAINTYPE.STATIC;
                train2.LeadLocomotive = lead;
                train.LeadLocomotive = null;
            }
            else
            {
                train2.TrainType = Train.TRAINTYPE.STATIC;
                train2.LeadLocomotive = null;
            }

            train.UpdateTrackActionsUncoupling(true);
            train2.UpdateTrackActionsUncoupling(false);
            if (MPManager.IsMultiPlayer()) { train.ControlMode = train2.ControlMode = Train.TRAIN_CONTROL.EXPLORER; }

            if (MPManager.IsMultiPlayer() && !MPManager.IsServer())
            {
                //add the new train to a list of uncoupled trains, handled specially
                if (PlayerLocomotive != null && PlayerLocomotive.Train == train2) MPManager.Instance().AddUncoupledTrains(train);
                else if (PlayerLocomotive != null && PlayerLocomotive.Train == train) MPManager.Instance().AddUncoupledTrains(train2);
            }


            train.CheckFreight();
            train2.CheckFreight();

            train.Update(0);   // stop the wheels from moving etc
            train2.Update(0);  // stop the wheels from moving etc

            car.SignalEvent(Event.Uncouple);
            // TODO which event should we fire
            //car.CreateEvent(62);  these are listed as alternate events
            //car.CreateEvent(63);
            if (MPManager.IsMultiPlayer())
            {
                MPManager.Notify((new MultiPlayer.MSGUncouple(train, train2, MultiPlayer.MPManager.GetUserName(), car.CarID, PlayerLocomotive)).ToString());
            }
            if (Confirmer.Viewer.IsReplaying) Confirmer.Confirm(CabControl.Uncouple, train.LastCar.CarID);
        }

        // uncouple to pre-defined train (AI - timetable mode only)
        public void UncoupleBehind(AITrain train, int noUnits, bool frontportion, AITrain newTrain, bool reverseTrain)
        {
            if (MPManager.IsMultiPlayer()) return;  // not allowed in MP

            // if front portion : move req units to new train and remove from old train
            // remove from rear to front otherwise they cannot be deleted

            var detachCar = train.Cars[noUnits];

            if (frontportion)
            {
                detachCar = train.Cars[noUnits];

                for (int iCar = 0; iCar <= noUnits - 1; iCar++)
                {
                    var car = train.Cars[0]; // each car is removed so always detach first car!!!
                    train.Cars.Remove(car);
                    train.Length = -car.LengthM;
                    newTrain.Cars.Add(car); // place in rear
                    car.Train = newTrain;
                    car.CarID = String.Copy(newTrain.Name);
                    newTrain.Length += car.LengthM;
                }
            }
            else
            {
                detachCar = train.Cars[train.Cars.Count - noUnits];
                int totalCars = train.Cars.Count;

                for (int iCar = 0; iCar <= noUnits - 1; iCar++)
                {
                    var car = train.Cars[totalCars - 1 - iCar]; // total cars is original length which keeps value despite cars are removed
                    train.Cars.Remove(car);
                    train.Length -= car.LengthM;
                    newTrain.Cars.Insert(0, car); // place in front
                    car.Train = newTrain;
                    car.CarID = String.Copy(newTrain.Name);
                    newTrain.Length += car.LengthM;
                }
            }

            train.LastCar.CouplerSlackM = 0;

            // and fix up the travellers
            if (frontportion)
            {
                train.CalculatePositionOfCars(0);
                newTrain.RearTDBTraveller = new Traveller(train.FrontTDBTraveller);
                newTrain.CalculatePositionOfCars(0);
            }
            else
            {
                newTrain.RearTDBTraveller = new Traveller(train.RearTDBTraveller);
                newTrain.CalculatePositionOfCars(0);
                train.RepositionRearTraveller();    // fix the rear traveller
            }

            if (reverseTrain)
            {
                newTrain.ReverseFormation(false);
            }

            train.UncoupledFrom = newTrain;
            newTrain.UncoupledFrom = train;
            newTrain.SpeedMpS = train.SpeedMpS = 0;
            newTrain.TrainMaxSpeedMpS = train.TrainMaxSpeedMpS;
            newTrain.AITrainBrakePercent = train.AITrainBrakePercent;
            newTrain.AITrainDirectionForward = true;

            train.CheckFreight();
            newTrain.CheckFreight();

            newTrain.MovementState = AITrain.AI_MOVEMENT_STATE.AI_STATIC; // start of as AI static
            newTrain.StartTime = null; // time will be set later

            detachCar.SignalEvent(Event.Uncouple);

            newTrain.TrainType = Train.TRAINTYPE.AI;
            newTrain.AI.TrainsToAdd.Add(newTrain);

            // update positions train
            TrackNode tn = train.FrontTDBTraveller.TN;
            float offset = train.FrontTDBTraveller.TrackNodeOffset;
            int direction = (int)train.FrontTDBTraveller.Direction;

            train.PresentPosition[0].SetTCPosition(tn.TCCrossReference, offset, direction);
            train.PresentPosition[0].RouteListIndex = train.ValidRoute[0].GetRouteIndex(train.PresentPosition[0].TCSectionIndex, 0);
            train.PresentPosition[0].CopyTo(ref train.PreviousPosition[0]);

            if (frontportion)
            {
                train.DistanceTravelledM -= newTrain.Length;
            }

            tn = train.RearTDBTraveller.TN;
            offset = train.RearTDBTraveller.TrackNodeOffset;
            direction = (int)train.RearTDBTraveller.Direction;

            train.PresentPosition[1].SetTCPosition(tn.TCCrossReference, offset, direction);
            train.PresentPosition[1].RouteListIndex = train.ValidRoute[0].GetRouteIndex(train.PresentPosition[1].TCSectionIndex, 0);

            // remove train from track and clear actions
            train.RemoveFromTrack();
            train.ClearActiveSectionItems();

            // set new track sections occupied
            Train.TCSubpathRoute tempRouteTrain = Signals.BuildTempRoute(train, train.PresentPosition[1].TCSectionIndex,
                train.PresentPosition[1].TCOffset, train.PresentPosition[1].TCDirection, train.Length, false, true, false);

            for (int iIndex = 0; iIndex < tempRouteTrain.Count; iIndex++)
            {
                TrackCircuitSection thisSection = Signals.TrackCircuitList[tempRouteTrain[iIndex].TCSectionIndex];
                thisSection.SetOccupied(train.routedForward);
            }

            // update positions new train
            tn = newTrain.FrontTDBTraveller.TN;
            offset = newTrain.FrontTDBTraveller.TrackNodeOffset;
            direction = (int)newTrain.FrontTDBTraveller.Direction;

            newTrain.PresentPosition[0].SetTCPosition(tn.TCCrossReference, offset, direction);
            newTrain.PresentPosition[0].RouteListIndex = newTrain.ValidRoute[0].GetRouteIndex(newTrain.PresentPosition[0].TCSectionIndex, 0);
            newTrain.PresentPosition[0].CopyTo(ref newTrain.PreviousPosition[0]);

            newTrain.DistanceTravelledM = 0.0f;

            tn = newTrain.RearTDBTraveller.TN;
            offset = newTrain.RearTDBTraveller.TrackNodeOffset;
            direction = (int)newTrain.RearTDBTraveller.Direction;

            newTrain.PresentPosition[1].SetTCPosition(tn.TCCrossReference, offset, direction);
            newTrain.PresentPosition[1].RouteListIndex = newTrain.ValidRoute[0].GetRouteIndex(newTrain.PresentPosition[1].TCSectionIndex, 0);

            // set track section occupied
            Train.TCSubpathRoute tempRouteNewTrain = Signals.BuildTempRoute(newTrain, newTrain.PresentPosition[1].TCSectionIndex,
                newTrain.PresentPosition[1].TCOffset, newTrain.PresentPosition[1].TCDirection, newTrain.Length, false, true, false);

            for (int iIndex = 0; iIndex < tempRouteNewTrain.Count; iIndex++)
            {
                TrackCircuitSection thisSection = Signals.TrackCircuitList[tempRouteNewTrain[iIndex].TCSectionIndex];
                thisSection.SetOccupied(newTrain.routedForward);
            }

        }

        /// <summary>
        /// Derive log-file name from route path and activity name
        /// </summary>

        public string DeriveLogFile(string appendix)
        {
            string logfilebase = String.Empty;
            string logfilefull = String.Empty;

            if (!String.IsNullOrEmpty(ActivityFileName))
            {
                logfilebase = String.Copy(UserSettings.UserDataFolder);
                logfilebase = String.Concat(logfilebase, "_", ActivityFileName);
            }
            else
            {
                logfilebase = String.Copy(UserSettings.UserDataFolder);
                logfilebase = String.Concat(logfilebase, "_explorer");
            }

            logfilebase = String.Concat(logfilebase, appendix);
            logfilefull = String.Concat(logfilebase, ".csv");

            bool logExists = File.Exists(logfilefull);
            int logCount = 0;

            while (logExists && logCount < 100)
            {
                logCount++;
                logfilefull = String.Concat(logfilebase, "_", logCount.ToString("00"), ".csv");
                logExists = File.Exists(logfilefull);
            }

            if (logExists) logfilefull = String.Empty;

            return (logfilefull);
        }

        /// <summary>
        /// Class TrainList extends class List<Train> with extra search methods
        /// </summary>

        public class TrainList : List<Train>
        {
            private Simulator simulator;

            /// <summary>
            /// basis constructor
            /// </summary>

            public TrainList(Simulator in_simulator)
                : base()
            {
                simulator = in_simulator;
            }

            /// <summary>
            /// Search and return TRAIN by number - any type
            /// </summary>

            public Train GetTrainByNumber(int reqNumber)
            {
                Train returnTrain = null;
                if (simulator.TrainDictionary.ContainsKey(reqNumber))
                {
                    returnTrain = simulator.TrainDictionary[reqNumber];
                }

                // dictionary is not always updated in normal activity and explorer mode, so double check
                // if not correct, search in the 'old' way
                if (returnTrain == null || returnTrain.Number != reqNumber)
                {
                    returnTrain = null;
                    for (int iTrain = 0; iTrain <= this.Count - 1; iTrain++)
                    {
                        if (this[iTrain].Number == reqNumber)
                            returnTrain = this[iTrain];
                    }
                }

                return (returnTrain);
            }

            /// <summary>
            /// Search and return Train by name - any type
            /// </summary>

            public Train GetTrainByName(string reqName)
            {
                Train returnTrain = null;
                if (simulator.NameDictionary.ContainsKey(reqName))
                {
                    returnTrain = simulator.NameDictionary[reqName];
                }

                return (returnTrain);
            }

            /// <summary>
            /// Check if numbered train is on startlist
            /// </summary>
            /// <param name="reqNumber"></param>
            /// <returns></returns>

            public Boolean CheckTrainNotStartedByNumber(int reqNumber)
            {
                return simulator.StartReference.Contains(reqNumber);
            }

            /// <summary>
            /// Search and return AITrain by number
            /// </summary>

            public AITrain GetAITrainByNumber(int reqNumber)
            {
                AITrain returnTrain = null;
                if (simulator.TrainDictionary.ContainsKey(reqNumber))
                {
                    returnTrain = simulator.TrainDictionary[reqNumber] as AITrain;
                }

                return (returnTrain);
            }

            /// <summary>
            /// Search and return AITrain by name
            /// </summary>

            public AITrain GetAITrainByName(string reqName)
            {
                AITrain returnTrain = null;
                if (simulator.NameDictionary.ContainsKey(reqName))
                {
                    returnTrain = simulator.NameDictionary[reqName] as AITrain;
                }

                return (returnTrain);
            }

        } // TrainList

    } // Simulator
}
