using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Amib.Threading;

using Intersect.Collections;
using Intersect.Config;
using Intersect.Enums;
using Intersect.Framework.Collections;
using Intersect.GameObjects;
using Intersect.GameObjects.Crafting;
using Intersect.GameObjects.Events;
using Intersect.GameObjects.Maps;
using Intersect.GameObjects.Maps.MapList;
using Intersect.Logging;
using Intersect.Logging.Output;
using Intersect.Models;
using Intersect.Server.Core;
using Intersect.Server.Database.GameData;
using Intersect.Server.Database.Logging;
using Intersect.Server.Database.PlayerData;
using Intersect.Server.Database.PlayerData.Players;
using Intersect.Server.Database.PlayerData.Security;
using Intersect.Server.Entities;
using Intersect.Server.General;
using Intersect.Server.Localization;
using Intersect.Server.Maps;
using Intersect.Server.Networking;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using MySqlConnector;

namespace Intersect.Server.Database
{

    public static partial class DbInterface
    {

        /// <summary>
        /// This is our thread pool for handling game-loop database interactions.
        /// Min/Max Number of Threads & Idle Timeouts are set via server config.
        /// </summary>
        public static SmartThreadPool Pool = new SmartThreadPool(
                new STPStartInfo()
                {
                    ThreadPoolName = "DatabasePool",
                    IdleTimeout = Options.Instance.Processing.DatabaseThreadIdleTimeout,
                    MinWorkerThreads = Options.Instance.Processing.MinDatabaseThreads,
                    MaxWorkerThreads = Options.Instance.Processing.MaxDatabaseThreads
                }
            );

        private const string GameDbFilename = "resources/gamedata.db";

        private const string PlayersDbFilename = "resources/playerdata.db";

        private static Logger gameDbLogger { get; set; }

        private static Logger playerDbLogger { get; set; }

        public static Dictionary<string, ServerVariableBase> ServerVariableEventTextLookup = new Dictionary<string, ServerVariableBase>();

        public static Dictionary<string, PlayerVariableBase> PlayerVariableEventTextLookup = new Dictionary<string, PlayerVariableBase>();

        public static Dictionary<string, GuildVariableBase> GuildVariableEventTextLookup = new Dictionary<string, GuildVariableBase>();

        public static ConcurrentDictionary<Guid, ServerVariableBase> UpdatedServerVariables = new ConcurrentDictionary<Guid, ServerVariableBase>();

        private static List<MapGrid> mapGrids = new List<MapGrid>();

        public static long RegisteredPlayers
        {
            get
            {
                using (var context = CreatePlayerContext())
                {
                    return context.Players.Count();
                }
            }
        }

        /// <summary>
        /// Creates a game context to query. Best practice is to scope this within a using statement.
        /// </summary>
        /// <param name="readOnly">Defines whether or not the context should initialize with change tracking. If readonly is true then SaveChanges will not work.</param>
        /// <returns></returns>
        public static GameContext CreateGameContext(
            bool readOnly = true,
            bool explicitLoad = false,
            bool lazyLoading = false,
            bool autoDetectChanges = false,
            QueryTrackingBehavior? queryTrackingBehavior = default
        )
        {
            return new GameContext(
                connectionStringBuilder: CreateConnectionStringBuilder(Options.GameDb ?? throw new InvalidOperationException(), GameDbFilename),
                databaseType: Options.GameDb.Type,
                readOnly: readOnly,
                explicitLoad: explicitLoad,
                lazyLoading: lazyLoading,
                autoDetectChanges: autoDetectChanges,
                queryTrackingBehavior: queryTrackingBehavior,
                logger: gameDbLogger,
                logLevel: Options.GameDb.LogLevel
            );
        }

        /// <summary>
        /// Creates a game context to query. Best practice is to scope this within a using statement.
        /// </summary>
        /// <param name="readOnly">Defines whether or not the context should initialize with change tracking. If readonly is true then SaveChanges will not work.</param>
        /// <returns></returns>
        public static PlayerContext CreatePlayerContext(bool readOnly = true, bool explicitLoad = false)
        {
            return new PlayerContext(
                    CreateConnectionStringBuilder(
                        Options.PlayerDb ?? throw new InvalidOperationException(), PlayersDbFilename
                    ), Options.PlayerDb.Type,
                    logger: playerDbLogger,
                    logLevel: Options.PlayerDb.LogLevel,
                    readOnly: readOnly,
                    explicitLoad: explicitLoad
                );
        }

        internal static LoggingContext CreateLoggingContext(bool readOnly = true)
        {
            return new LoggingContext(
                Logging.LoggingContext.DefaultConnectionStringBuilder,
                DatabaseOptions.DatabaseType.SQLite,
                readOnly: readOnly
            );
        }

        public static void InitializeDbLoggers()
        {
            if (Options.GameDb.LogLevel > LogLevel.None)
            {
                gameDbLogger = new Logger(
                    new LogConfiguration
                    {
                        Tag = "GAMEDB",
                        LogLevel = Options.GameDb.LogLevel,
                        Outputs = ImmutableList.Create<ILogOutput>(
                            new FileOutput(Log.SuggestFilename(null, "gamedb"), LogLevel.Debug)
                        )
                    }
                );
            }

            if (Options.PlayerDb.LogLevel > LogLevel.None)
            {
                playerDbLogger = new Logger(
                    new LogConfiguration
                    {
                        Tag = "PLAYERDB",
                        LogLevel = Options.PlayerDb.LogLevel,
                        Outputs = ImmutableList.Create<ILogOutput>(
                            new FileOutput(Log.SuggestFilename(null, "playerdb"), LogLevel.Debug)
                        )
                    }
                );
            }
        }

        //Check Directories
        public static void CheckDirectories()
        {
            if (!Directory.Exists("resources"))
            {
                Directory.CreateDirectory("resources");
            }
        }

        //As of now Database writes only occur on player saving & when editors make game changes
        //Database writes are actually pretty rare. And even player saves are offloaded as tasks so
        //if delayed it won't matter much.
        //TODO: Options for saving frequency and number of backups to keep.
        public static void BackupDatabase()
        {
        }

        public static DbConnectionStringBuilder CreateConnectionStringBuilder(
            DatabaseOptions databaseOptions,
            string filename
        )
        {
            switch (databaseOptions.Type)
            {
                case DatabaseOptions.DatabaseType.SQLite:
                    return new SqliteConnectionStringBuilder($"Data Source={filename}");

                case DatabaseOptions.DatabaseType.MySQL:
                    return new MySqlConnectionStringBuilder
                    {
                        Server = databaseOptions.Server,
                        Port = databaseOptions.Port,
                        Database = databaseOptions.Database,
                        UserID = databaseOptions.Username,
                        Password = databaseOptions.Password
                    };

                default:
                    throw new ArgumentOutOfRangeException(nameof(databaseOptions.Type));
            }
        }

        // Database setup, version checking
        internal static bool InitDatabase(IServerContext serverContext)
        {
            using (var gameContext = CreateGameContext(readOnly: false))
            {

                using (var playerContext = CreatePlayerContext(readOnly: false))
                {

                    Logging.LoggingContext.Configure(
                        DatabaseOptions.DatabaseType.SQLite, Logging.LoggingContext.DefaultConnectionStringBuilder
                    );

                    // We don't want anyone running the old migration tool accidentally
                    try
                    {
                        if (File.Exists("Intersect Migration Tool.exe"))
                        {
                            File.Delete("Intersect Migration Tool.exe");
                        }

                        if (File.Exists("Intersect Migration Tool.pdb"))
                        {
                            File.Delete("Intersect Migration Tool.pdb");
                        }

                        if (File.Exists("Intersect Migration Tool.mdb"))
                        {
                            File.Delete("Intersect Migration Tool.mdb");
                        }
                    }
                    catch
                    {
                        // ignored
                    }

                    var gameMigrations = gameContext.PendingMigrations;
                    var showGameMigrationWarning = gameMigrations.Any() && !gameMigrations.Contains("20180905042857_Initial");
                    var playerMigrations = playerContext.PendingMigrations;
                    var showPlayerMigrationWarning =
                        playerMigrations.Any() && !playerMigrations.Contains("20180927161502_InitialPlayerDb");

                    if (showGameMigrationWarning || showPlayerMigrationWarning)
                    {
                        Console.WriteLine();
                        Console.WriteLine(Strings.Database.upgraderequired);
                        Console.WriteLine(
                            Strings.Database.upgradebackup.ToString(Strings.Database.upgradeready, Strings.Database.upgradeexit)
                        );

                        Console.WriteLine();
                        while (true)
                        {
                            Console.Write("> ");
                            var input = Console.ReadLine().Trim();
                            if (input == Strings.Database.upgradeready.ToString().Trim())
                            {
                                break;
                            }
                            else if (input.ToLower() == Strings.Database.upgradeexit.ToString().Trim().ToLower())
                            {
                                Environment.Exit(1);

                                return false;
                            }
                        }

                        Console.WriteLine();
                        Console.WriteLine(
                            "Please wait! Migrations can take several minutes, and even longer if you are using MySQL databases!"
                        );
                    }

                    var gameContextMigrationScheduler = new MigrationScheduler<GameContext>(gameContext);
                    gameContextMigrationScheduler.SchedulePendingMigrations();
                    gameContextMigrationScheduler.ApplyScheduledMigrations();
                    var remainingGameMigrations = gameContext.PendingMigrations;
                    var processedGameMigrations = new List<string>(gameMigrations);
                    foreach (var itm in remainingGameMigrations)
                    {
                        processedGameMigrations.Remove(itm);
                    }

                    gameContext.MigrationsProcessed(processedGameMigrations.ToArray());

#if DEBUG
                    if (ServerContext.Instance.RestApi.Configuration.SeedMode)
                    {
                        gameContext.Seed();
                    }
#endif

                    playerContext.Database.Migrate();
                    var remainingPlayerMigrations = playerContext.PendingMigrations;
                    var processedPlayerMigrations = new List<string>(playerMigrations);
                    foreach (var itm in remainingPlayerMigrations)
                    {
                        processedPlayerMigrations.Remove(itm);
                    }

                    playerContext.MigrationsProcessed(processedPlayerMigrations.ToArray());
#if DEBUG
                    if (ServerContext.Instance.RestApi.Configuration.SeedMode)
                    {
                        playerContext.Seed();
                    }
#endif

                    try
                    {
                        using (var loggingContext = LoggingContext.Create())
                        {
                            loggingContext.Database?.Migrate();

#if DEBUG
                            if (ServerContext.Instance.RestApi.Configuration.SeedMode)
                            {
                                loggingContext.Seed();
                            }
#endif
                        }
                    }
                    catch (Exception exception)
                    {
                        throw;
                    }

                    LoadAllGameObjects();
                    LoadTime();
                    OnClassesLoaded();
                    OnMapsLoaded();
                    CacheServerVariableEventTextLookups();
                    CachePlayerVariableEventTextLookups();
                    CacheGuildVariableEventTextLookups();
                }
            }

            return true;
        }

        public static Client GetPlayerClient(string username)
        {
            //Try to fetch a player entity by username, online or offline.
            //Check Online First
            lock (Globals.ClientLock)
            {
                foreach (var client in Globals.Clients)
                {
                    if (client.Entity != null && client.Name.ToLower() == username.ToLower())
                    {
                        return client;
                    }
                }
            }

            return null;
        }

        public static void SetPlayerPower(string username, UserRights power)
        {
            var user = User.Find(username);
            if (user != null)
            {
                user.Power = power;
                user.Save();
            }
            else
            {
                Console.WriteLine(Strings.Account.doesnotexist);
            }
        }

        public static bool SetPlayerPower(User user, UserRights power)
        {
            if (user != null)
            {
                user.Power = power;
                user.Save();

                return true;
            }
            else
            {
                Console.WriteLine(Strings.Account.doesnotexist);

                return false;
            }
        }

        //User Info
        public static bool AccountExists(string accountname)
        {
            return User.Find(accountname) != null;
        }

        public static string UsernameFromEmail(string email)
        {
            var user = User.FindFromEmail(email);
            if (user != null)
            {
                return user.Name;
            }
            return null;
        }

        public static Player GetUserCharacter(User user, Guid playerId, bool explicitLoad = false)
        {
            if (user == default)
            {
                return default;
            }

            foreach (var player in user.Players)
            {
                if (player.Id != playerId)
                {
                    continue;
                }

                if (explicitLoad)
                {
                    try
                    {
                        using var playerContext = CreatePlayerContext(readOnly: true, explicitLoad: false);
                        var playerEntry = playerContext.Players.Attach(player);
                        playerEntry.Collection(p => p.Items).Query().Load();
                        playerEntry.Collection(player => player.Bank).Load();
                        playerEntry.Collection(player => player.Hotbar).Load();
                        playerEntry.Collection(player => player.Items).Load();
                        playerEntry.Collection(player => player.Quests).Load();
                        playerEntry.Collection(player => player.Spells).Load();
                        playerEntry.Collection(player => player.Variables).Load();
                        _ = Player.Validate(player);
                    }
                    catch (Exception exception)
                    {
                        Debugger.Break();
                        Log.Error(exception);
                        throw new Exception($"Error during explicit load of player {BitConverter.ToString(playerId.ToByteArray()).Replace("-", string.Empty)}", exception);
                    }
                }

                return player;
            }

            return default;
        }

        public static void CreateAccount(
            Client client,
            string username,
            string password,
            string email
        )
        {
            var sha = new SHA256Managed();

            //Generate a Salt
            var rng = new RNGCryptoServiceProvider();
            var buff = new byte[20];
            rng.GetBytes(buff);
            var salt = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Convert.ToBase64String(buff))))
                .Replace("-", "");

            var user = new User
            {
                Name = username,
                Email = email,
                Salt = salt,
                Password = User.SaltPasswordHash(password, salt),
                Power = UserRights.None,
            };

            if (User.Count() == 0)
            {
                user.Power = UserRights.Admin;
            }

            user.Save(create: true);

            client?.SetUser(user);
        }

        public static void ResetPass(User user, string password)
        {
            var sha = new SHA256Managed();

            //Generate a Salt
            var rng = new RNGCryptoServiceProvider();
            var buff = new byte[20];
            rng.GetBytes(buff);
            var salt = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Convert.ToBase64String(buff))))
                .Replace("-", "");

            user.Salt = salt;
            user.Password = User.SaltPasswordHash(password, salt);
            user.Save();
        }

        public static bool BagEmpty(Bag bag)
        {
            for (var i = 0; i < bag.Slots.Count; i++)
            {
                if (bag.Slots[i] != null)
                {
                    var item = ItemBase.Get(bag.Slots[i].ItemId);
                    if (item != null)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        //Game Object Saving/Loading
        private static void LoadAllGameObjects()
        {
            try
            {
                foreach (var descriptorType in Enum.GetValues<GameObjectType>())
                {
                    LoadGameObjects(descriptorType);
                }
            }
            catch (Exception exception)
            {
                Log.Error(exception);
                throw;
            }
        }

        private static readonly MethodInfo _descriptorLoadGameObjects = typeof(DbInterface).GetMethod(
            nameof(LoadGameObjects),
            1,
            BindingFlags.NonPublic | BindingFlags.Static,
            default,
            new[] { typeof(GameObjectType) },
            default
        ) ?? throw new InvalidOperationException();

        private static void LoadGameObjects<TDescriptor>(
            GameObjectType descriptorType
        ) where TDescriptor : Descriptor
        {
            var databaseObjectLookup = descriptorType.GetLookup();
            databaseObjectLookup.Clear();

            using var gameContext = CreateGameContext(
                readOnly: true,
                explicitLoad: true,
                lazyLoading: true,
                autoDetectChanges: true,
                queryTrackingBehavior: QueryTrackingBehavior.TrackAll
            );

            var dbSet = gameContext.GetDbSet<TDescriptor>();

            var folders = gameContext.Folders
                .Where(folder => folder.DescriptorType == descriptorType)
                .Include(folder => folder.Name)
                .ThenInclude(name => name.Localizations)
                .ToList();

            var descriptors = dbSet
                .Include(descriptor => descriptor.Parent)
                .ToList();

            foreach (var folder in folders)
            {
                folder.LinkChildren(folders.Cast<IFolderable>().Concat(descriptors.Cast<IFolderable>()));
            }

            foreach (var descriptor in descriptors)
            {
                _ = databaseObjectLookup.Set(descriptor.Id, descriptor);

                switch (descriptor)
                {
                    case MapController mapController:
                        if (Options.Instance.MapOpts.Layers.DestroyOrphanedLayers)
                        {
                            mapController.DestroyOrphanedLayers();
                        }
                        break;
                }
            }
        }

        private static void LoadGameObjects(GameObjectType descriptorType)
        {
            if (descriptorType == GameObjectType.Time)
            {
                return;
            }

            try
            {
                var descriptorClrType = descriptorType.GetObjectType();
                switch (descriptorType)
                {
                    case GameObjectType.Map:
                        descriptorClrType = typeof(MapController);
                        break;
                }

                var loaderMethod = _descriptorLoadGameObjects.MakeGenericMethod(descriptorClrType);
                _ = loaderMethod.Invoke(default, new object[] { descriptorType });
            }
            catch (Exception exception)
            {
                Log.Error(exception);
                throw;
            }
        }

        public static IDatabaseObject AddGameObject(GameObjectType gameObjectType)
        {
            return AddGameObject(gameObjectType, Guid.Empty);
        }

        public static IDatabaseObject AddGameObject(GameObjectType gameObjectType, Guid predefinedid)
        {
            if (predefinedid == Guid.Empty)
            {
                predefinedid = Guid.NewGuid();
            }

            IDatabaseObject dbObj = null;
            switch (gameObjectType)
            {
                case GameObjectType.Animation:
                    dbObj = new AnimationBase(predefinedid);

                    break;
                case GameObjectType.Class:
                    dbObj = new ClassBase(predefinedid);

                    break;
                case GameObjectType.Item:
                    dbObj = new ItemBase(predefinedid);

                    break;
                case GameObjectType.Npc:
                    dbObj = new NpcBase(predefinedid);

                    break;
                case GameObjectType.Projectile:
                    dbObj = new ProjectileBase(predefinedid);

                    break;
                case GameObjectType.Resource:
                    dbObj = new ResourceBase(predefinedid);

                    break;
                case GameObjectType.Shop:
                    dbObj = new ShopBase(predefinedid);

                    break;
                case GameObjectType.Spell:
                    dbObj = new SpellBase(predefinedid);

                    break;
                case GameObjectType.CraftTables:
                    dbObj = new CraftingTableBase(predefinedid);

                    break;
                case GameObjectType.Crafts:
                    dbObj = new CraftBase(predefinedid);

                    break;
                case GameObjectType.Map:
                    dbObj = new MapController(predefinedid);

                    break;
                case GameObjectType.Event:
                    dbObj = new EventBase(predefinedid);

                    break;
                case GameObjectType.PlayerVariable:
                    dbObj = new PlayerVariableBase(predefinedid);

                    break;
                case GameObjectType.ServerVariable:
                    dbObj = new ServerVariableBase(predefinedid);

                    break;
                case GameObjectType.Tileset:
                    dbObj = new TilesetBase(predefinedid);

                    break;
                case GameObjectType.Time:
                    break;

                case GameObjectType.Quest:
                    dbObj = new QuestBase(predefinedid);
                    ((QuestBase)dbObj).StartEvent = (EventBase)AddGameObject(GameObjectType.Event);
                    ((QuestBase)dbObj).EndEvent = (EventBase)AddGameObject(GameObjectType.Event);
                    ((QuestBase)dbObj).StartEvent.CommonEvent = false;
                    ((QuestBase)dbObj).EndEvent.CommonEvent = false;

                    break;

                case GameObjectType.GuildVariable:
                    dbObj = new GuildVariableBase(predefinedid);

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(gameObjectType), gameObjectType, null);
            }

            return dbObj == null ? null : AddGameObject(gameObjectType, dbObj);
        }

        public static IDatabaseObject AddGameObject(GameObjectType gameObjectType, IDatabaseObject dbObj)
        {
            try
            {
                using (var context = CreateGameContext(readOnly: false))
                {

                    switch (gameObjectType)
                    {
                        case GameObjectType.Animation:
                            context.Animations.Add((AnimationBase)dbObj);
                            AnimationBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.Class:
                            context.Classes.Add((ClassBase)dbObj);
                            ClassBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.Item:
                            context.Items.Add((ItemBase)dbObj);
                            ItemBase.Lookup.Set(dbObj.Id, dbObj);

                            break;
                        case GameObjectType.Npc:
                            context.Npcs.Add((NpcBase)dbObj);
                            NpcBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.Projectile:
                            context.Projectiles.Add((ProjectileBase)dbObj);
                            ProjectileBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.Quest:
                            context.Quests.Add((QuestBase)dbObj);
                            QuestBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.Resource:
                            context.Resources.Add((ResourceBase)dbObj);
                            ResourceBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.Shop:
                            context.Shops.Add((ShopBase)dbObj);
                            ShopBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.Spell:
                            context.Spells.Add((SpellBase)dbObj);
                            SpellBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.CraftTables:
                            context.CraftingTables.Add((CraftingTableBase)dbObj);
                            CraftingTableBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.Crafts:
                            context.Crafts.Add((CraftBase)dbObj);
                            CraftBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.Map:
                            context.Maps.Add((MapController)dbObj);
                            MapController.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.Event:
                            context.Events.Add((EventBase)dbObj);
                            EventBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.PlayerVariable:
                            context.PlayerVariables.Add((PlayerVariableBase)dbObj);
                            PlayerVariableBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.ServerVariable:
                            context.ServerVariables.Add((ServerVariableBase)dbObj);
                            ServerVariableBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.Tileset:
                            context.Tilesets.Add((TilesetBase)dbObj);
                            TilesetBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        case GameObjectType.Time:
                            break;

                        case GameObjectType.GuildVariable:
                            context.GuildVariables.Add((GuildVariableBase)dbObj);
                            GuildVariableBase.Lookup.Set(dbObj.Id, dbObj);

                            break;

                        default:
                            throw new ArgumentOutOfRangeException(nameof(gameObjectType), gameObjectType, null);
                    }

                    context.ChangeTracker.DetectChanges();
                    context.Entry(dbObj).State = EntityState.Added;
                    context.SaveChanges();
                }

                return dbObj;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                throw;
            }
        }

        public static void DeleteGameObject(IDatabaseObject gameObject)
        {
            try
            {
                using (var context = CreateGameContext(readOnly: false))
                {
                    switch (gameObject.Type)
                    {
                        case GameObjectType.Animation:
                            context.Animations.Remove((AnimationBase)gameObject);

                            break;
                        case GameObjectType.Class:
                            context.Classes.Remove((ClassBase)gameObject);

                            break;
                        case GameObjectType.Item:
                            context.Items.Remove((ItemBase)gameObject);

                            break;
                        case GameObjectType.Npc:
                            context.Npcs.Remove((NpcBase)gameObject);

                            break;
                        case GameObjectType.Projectile:
                            context.Projectiles.Remove((ProjectileBase)gameObject);

                            break;
                        case GameObjectType.Quest:

                            if (((QuestBase)gameObject).StartEvent != null)
                            {
                                context.Events.Remove(((QuestBase)gameObject).StartEvent);
                                context.Entry(((QuestBase)gameObject).StartEvent).State = EntityState.Deleted;
                                EventBase.Lookup.Delete(((QuestBase)gameObject).StartEvent);
                            }

                            if (((QuestBase)gameObject).EndEvent != null)
                            {
                                context.Events.Remove(((QuestBase)gameObject).EndEvent);
                                context.Entry(((QuestBase)gameObject).EndEvent).State = EntityState.Deleted;
                                EventBase.Lookup.Delete(((QuestBase)gameObject).EndEvent);
                            }

                            foreach (var tsk in ((QuestBase)gameObject).Tasks)
                            {
                                if (tsk.CompletionEvent != null)
                                {
                                    context.Events.Remove(tsk.CompletionEvent);
                                    context.Entry(tsk.CompletionEvent).State = EntityState.Deleted;
                                    EventBase.Lookup.Delete(tsk.CompletionEvent);
                                }
                            }

                            context.Quests.Remove((QuestBase)gameObject);

                            break;
                        case GameObjectType.Resource:
                            context.Resources.Remove((ResourceBase)gameObject);

                            break;
                        case GameObjectType.Shop:
                            context.Shops.Remove((ShopBase)gameObject);

                            break;
                        case GameObjectType.Spell:
                            context.Spells.Remove((SpellBase)gameObject);

                            break;
                        case GameObjectType.CraftTables:
                            context.CraftingTables.Remove((CraftingTableBase)gameObject);

                            break;
                        case GameObjectType.Crafts:
                            context.Crafts.Remove((CraftBase)gameObject);

                            break;
                        case GameObjectType.Map:
                            context.Maps.Remove((MapController)gameObject);
                            MapController.Lookup.Delete(gameObject);

                            break;
                        case GameObjectType.Event:
                            context.Events.Remove((EventBase)gameObject);

                            break;
                        case GameObjectType.PlayerVariable:
                            context.PlayerVariables.Remove((PlayerVariableBase)gameObject);

                            break;
                        case GameObjectType.ServerVariable:
                            context.ServerVariables.Remove((ServerVariableBase)gameObject);

                            break;
                        case GameObjectType.Tileset:
                            context.Tilesets.Remove((TilesetBase)gameObject);

                            break;
                        case GameObjectType.Time:
                            break;
                        case GameObjectType.GuildVariable:
                            context.GuildVariables.Remove((GuildVariableBase)gameObject);

                            break;
                    }

                    if (gameObject.Type.GetLookup().Values.Contains(gameObject))
                    {
                        if (!gameObject.Type.GetLookup().Delete(gameObject))
                        {
                            throw new Exception();
                        }
                    }

                    context.ChangeTracker.DetectChanges();
                    context.Entry(gameObject).State = EntityState.Deleted;
                    context.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                throw;
            }
        }

        public static void SaveGameObject(IDatabaseObject gameObject)
        {
            try
            {
                using (var context = CreateGameContext(readOnly: false))
                {

                    switch (gameObject.Type)
                    {
                        case GameObjectType.Animation:
                            context.Animations.Update((AnimationBase)gameObject);

                            break;
                        case GameObjectType.Class:
                            context.Classes.Update((ClassBase)gameObject);

                            break;
                        case GameObjectType.Item:
                            context.Items.Update((ItemBase)gameObject);

                            break;
                        case GameObjectType.Npc:
                            context.Npcs.Update((NpcBase)gameObject);

                            break;
                        case GameObjectType.Projectile:
                            context.Projectiles.Update((ProjectileBase)gameObject);

                            break;
                        case GameObjectType.Quest:

                            if (((QuestBase)gameObject).StartEvent != null)
                            {
                                context.Events.Update(((QuestBase)gameObject).StartEvent);
                            }

                            if (((QuestBase)gameObject).EndEvent != null)
                            {
                                context.Events.Update(((QuestBase)gameObject).EndEvent);
                            }

                            foreach (var tsk in ((QuestBase)gameObject).Tasks)
                            {
                                if (tsk.CompletionEvent != null)
                                {
                                    context.Events.Update(tsk.CompletionEvent);
                                }
                            }

                            context.Quests.Update((QuestBase)gameObject);

                            break;
                        case GameObjectType.Resource:
                            context.Resources.Update((ResourceBase)gameObject);

                            break;
                        case GameObjectType.Shop:
                            context.Shops.Update((ShopBase)gameObject);

                            break;
                        case GameObjectType.Spell:
                            context.Spells.Update((SpellBase)gameObject);

                            break;
                        case GameObjectType.CraftTables:
                            context.CraftingTables.Update((CraftingTableBase)gameObject);

                            break;
                        case GameObjectType.Crafts:
                            context.Crafts.Update((CraftBase)gameObject);

                            break;
                        case GameObjectType.Map:
                            context.Maps.Update((MapController)gameObject);

                            break;
                        case GameObjectType.Event:
                            context.Events.Update((EventBase)gameObject);

                            break;
                        case GameObjectType.PlayerVariable:
                            context.PlayerVariables.Update((PlayerVariableBase)gameObject);

                            break;
                        case GameObjectType.ServerVariable:
                            context.ServerVariables.Update((ServerVariableBase)gameObject);

                            break;
                        case GameObjectType.Tileset:
                            context.Tilesets.Update((TilesetBase)gameObject);

                            break;
                        case GameObjectType.Time:
                            break;
                        case GameObjectType.GuildVariable:
                            context.GuildVariables.Update((GuildVariableBase)gameObject);

                            break;
                    }

                    context.ChangeTracker.DetectChanges();
                    context.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                throw;
            }
        }

        //Post Loading Functions
        private static void OnMapsLoaded()
        {
            if (MapBase.Lookup.Count == 0)
            {
                Console.WriteLine(Strings.Database.nomaps);
                AddGameObject(GameObjectType.Map);
            }

            GenerateMapGrids();
            LoadMapFolders();
            CheckAllMapConnections();

            foreach (var map in MapController.Lookup)
            {
                ((MapController)map.Value).Initialize();
            }
        }

        private static void OnClassesLoaded()
        {
            if (ClassBase.Lookup.Count == 0)
            {
                Console.WriteLine(Strings.Database.noclasses);
                var cls = (ClassBase)AddGameObject(GameObjectType.Class);
                cls.Name = Strings.Database.Default;
                var defaultMale = new ClassSprite()
                {
                    Sprite = "Base_Male.png",
                    Gender = Gender.Male
                };

                var defaultFemale = new ClassSprite()
                {
                    Sprite = "Base_Female.png",
                    Gender = Gender.Female
                };

                cls.Sprites.Add(defaultMale);
                cls.Sprites.Add(defaultFemale);
                for (var i = 0; i < (int)Vitals.VitalCount; i++)
                {
                    cls.BaseVital[i] = 20;
                }

                for (var i = 0; i < (int)Stats.StatCount; i++)
                {
                    cls.BaseStat[i] = 20;
                }
                SaveGameObject(cls);
            }
        }

        public static void CachePlayerVariableEventTextLookups()
        {
            var lookup = new Dictionary<string, PlayerVariableBase>();
            var addedIds = new HashSet<string>();
            foreach (PlayerVariableBase variable in PlayerVariableBase.Lookup.Values)
            {
                if (!string.IsNullOrWhiteSpace(variable.TextId) && !addedIds.Contains(variable.TextId))
                {
                    lookup.Add(Strings.Events.playervar + "{" + variable.TextId + "}", variable);
                    lookup.Add(Strings.Events.playerswitch + "{" + variable.TextId + "}", variable);
                    addedIds.Add(variable.TextId);
                }
            }
            PlayerVariableEventTextLookup = lookup;
        }

        public static void CacheServerVariableEventTextLookups()
        {
            var lookup = new Dictionary<string, ServerVariableBase>();
            var addedIds = new HashSet<string>();
            foreach (ServerVariableBase variable in ServerVariableBase.Lookup.Values)
            {
                if (!string.IsNullOrWhiteSpace(variable.TextId) && !addedIds.Contains(variable.TextId))
                {
                    lookup.Add(Strings.Events.globalvar + "{" + variable.TextId + "}", variable);
                    lookup.Add(Strings.Events.globalswitch + "{" + variable.TextId + "}", variable);
                    addedIds.Add(variable.TextId);
                }
            }
            ServerVariableEventTextLookup = lookup;
        }

        public static void CacheGuildVariableEventTextLookups()
        {
            var lookup = new Dictionary<string, GuildVariableBase>();
            var addedIds = new HashSet<string>();
            foreach (GuildVariableBase variable in GuildVariableBase.Lookup.Values)
            {
                if (!string.IsNullOrWhiteSpace(variable.TextId) && !addedIds.Contains(variable.TextId))
                {
                    lookup.Add(Strings.Events.guildvar + "{" + variable.TextId + "}", variable);
                    addedIds.Add(variable.TextId);
                }
            }
            GuildVariableEventTextLookup = lookup;
        }

        //Extra Map Helper Functions
        public static void CheckAllMapConnections()
        {
            var changed = false;
            foreach (MapController map in MapController.Lookup.Values)
            {
                CheckMapConnections(map, MapController.Lookup);
            }
        }

        public static bool CheckMapConnections(MapController map, DatabaseObjectLookup maps)
        {
            var updated = false;
            if (!maps.Keys.Contains(map.Up) && map.Up != Guid.Empty)
            {
                map.Up = Guid.Empty;
                updated = true;
            }

            if (!maps.Keys.Contains(map.Down) && map.Down != Guid.Empty)
            {
                map.Down = Guid.Empty;
                updated = true;
            }

            if (!maps.Keys.Contains(map.Left) && map.Left != Guid.Empty)
            {
                map.Left = Guid.Empty;
                updated = true;
            }

            if (!maps.Keys.Contains(map.Right) && map.Right != Guid.Empty)
            {
                map.Right = Guid.Empty;
                updated = true;
            }

            if (updated)
            {
                SaveGameObject(map);
                PacketSender.SendMapToEditors(map.Id);
                return true;
            }

            return false;
        }

        public static void GenerateMapGrids()
        {
            lock (mapGrids)
            {
                mapGrids.Clear();
                foreach (var map in MapController.Lookup.Values)
                {
                    if (mapGrids.Count == 0)
                    {
                        mapGrids.Add(new MapGrid(map.Id, 0));
                    }
                    else
                    {
                        for (var y = 0; y < mapGrids.Count; y++)
                        {
                            if (!mapGrids[y].HasMap(map.Id))
                            {
                                if (y != mapGrids.Count - 1)
                                {
                                    continue;
                                }

                                mapGrids.Add(new MapGrid(map.Id, mapGrids.Count));

                                break;
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }

                foreach (MapController map in MapController.Lookup.Values)
                {
                    lock (map.GetMapLock())
                    {
                        var myGrid = map.MapGrid;
                        var surroundingMapIds = new List<Guid>();
                        var surroundingMaps = new List<MapController>();
                        for (var x = map.MapGridX - 1; x <= map.MapGridX + 1; x++)
                        {
                            for (var y = map.MapGridY - 1; y <= map.MapGridY + 1; y++)
                            {
                                if (x == map.MapGridX && y == map.MapGridY)
                                {
                                    continue;
                                }

                                if (x >= mapGrids[myGrid].XMin &&
                                    x < mapGrids[myGrid].XMax &&
                                    y >= mapGrids[myGrid].YMin &&
                                    y < mapGrids[myGrid].YMax &&
                                    mapGrids[myGrid].MyGrid[x, y] != Guid.Empty)
                                {

                                    surroundingMapIds.Add(mapGrids[myGrid].MyGrid[x, y]);
                                    surroundingMaps.Add(MapController.Get(mapGrids[myGrid].MyGrid[x, y]));
                                }
                            }
                        }
                        map.SurroundingMapIds = surroundingMapIds.ToArray();
                        map.SurroundingMaps = surroundingMaps.ToArray();
                    }
                }

                for (var i = 0; i < mapGrids.Count; i++)
                {
                    PacketSender.SendMapGridToAll(i);
                }
            }
        }

        public static MapGrid GetGrid(int index)
        {
            lock (mapGrids)
            {
                return mapGrids[index];
            }
        }

        public static bool GridsContain(Guid id)
        {
            lock (mapGrids)
            {
                return mapGrids.Any(g => g.HasMap(id));
            }
        }

        //Map Folders
        private static void LoadMapFolders()
        {
            try
            {
                using (var context = CreateGameContext(readOnly: true))
                {
                    var rootMapList = new MapList();

                    var folders = context.Folders
                        .Where(folder => folder.DescriptorType == GameObjectType.Map)
                        .Include(folder => folder.Name)
                        .ThenInclude(name => name.Localizations)
                        .AsNoTrackingWithIdentityResolution()
                        .ToList();

                    var rootFolders = folders
                        .Where(folder => folder.Parent == default);

                    MapListMap LoadMap(MapList mapList, MapBase mapDescriptor)
                    {
                        return mapList.AddMap(mapDescriptor, MapBase.Lookup);
                    }

                    MapListFolder LoadFolder(MapList mapList, Folder folder)
                    {
                        var parentId = folder.Id;
                        var mapListFolder = mapList.AddFolder(folder);

                        foreach (var subfolder in folders.Where(folder => folder.ParentId == parentId))
                        {
                            _ = LoadFolder(mapListFolder.Children, subfolder);
                        }

                        foreach (var map in context.Maps.Where(map => map.ParentId == parentId))
                        {
                            _ = LoadMap(mapListFolder.Children, map);
                        }

                        return mapListFolder;
                    }

                    foreach (var folder in rootFolders)
                    {
                        _ = LoadFolder(rootMapList, folder);
                    }

                    var folderlessMaps = context.Maps
                        .Where(map => map.Parent == default);

                    foreach (var map in folderlessMaps)
                    {
                        _ = LoadMap(rootMapList, map);
                    }

                    MapList.List = rootMapList;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                throw;
            }

            MapList.List.PostLoad(MapBase.Lookup, true, true);
            PacketSender.SendMapListToAll();
        }

        public static void SaveMapList()
        {
            try
            {
                //using (var context = CreateGameContext(readOnly: false))
                //{
                //    context.MapFolders.Update(MapList.List);
                //    context.ChangeTracker.DetectChanges();
                //    context.SaveChanges();
                //}
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                throw;
            }
        }

        //Time
        private static void LoadTime()
        {
            try
            {
                using (var context = CreateGameContext(readOnly: false))
                {
                    var time = context.Time.FirstOrDefault();
                    if (time == null)
                    {
                        context.Time.Add(TimeBase.GetTimeBase());
                        context.ChangeTracker.DetectChanges();
                        context.SaveChanges();
                    }
                    else
                    {
                        TimeBase.SetStaticTime(time);
                    }
                }
                General.Time.Init();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                throw;
            }
        }

        public static void SaveTime()
        {
            try
            {
                using (var context = CreateGameContext(readOnly: false))
                {
                    context.Time.Update(TimeBase.GetTimeBase());
                    context.ChangeTracker.DetectChanges();
                    context.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                throw;
            }
        }

        public static void SaveUpdatedServerVariables()
        {
            if (UpdatedServerVariables.Count > 0)
            {
                using (var context = CreateGameContext(readOnly: false))
                {
                    foreach (var variable in UpdatedServerVariables)
                    {
                        var serverVar = variable.Value;
                        if (serverVar != null)
                        {
                            context.ServerVariables.Update(variable.Value);
                        }
                        UpdatedServerVariables.TryRemove(variable.Key, out ServerVariableBase obj);
                    }
                    context.SaveChanges();
                }
            }
        }

        //Migration Code
        public static void Migrate(DatabaseOptions orig, DatabaseOptions.DatabaseType convType)
        {
            var gameDb = orig == Options.GameDb;
            PlayerContext newPlayerContext = null;
            GameContext newGameContext = null;
            var newOpts = new DatabaseOptions
            {
                Type = DatabaseOptions.DatabaseType.SQLite
            };

            //MySql Creds
            string host, user, pass, database;
            ushort port;
            var dbConnected = false;

            switch (convType)
            {
                case DatabaseOptions.DatabaseType.MySQL:
                    while (!dbConnected)
                    {
                        Console.WriteLine(Strings.Migration.entermysqlinfo);
                        Console.Write(Strings.Migration.mysqlhost);
                        host = Console.ReadLine()?.Trim() ?? "localhost";
                        Console.Write(Strings.Migration.mysqlport);
                        var portinput = Console.ReadLine()?.Trim();
                        if (string.IsNullOrWhiteSpace(portinput))
                        {
                            portinput = "3306";
                        }

                        port = ushort.Parse(portinput);
                        Console.Write(Strings.Migration.mysqldatabase);
                        database = Console.ReadLine()?.Trim();
                        Console.Write(Strings.Migration.mysqluser);
                        user = Console.ReadLine().Trim();
                        Console.Write(Strings.Migration.mysqlpass);
                        pass = GetPassword();

                        Console.WriteLine();
                        Console.WriteLine(Strings.Migration.mysqlconnecting);
                        var connectionStringBuilder = CreateConnectionStringBuilder(
                            new DatabaseOptions
                            {
                                Type = DatabaseOptions.DatabaseType.MySQL,
                                Server = host,
                                Port = port,
                                Database = database,
                                Username = user,
                                Password = pass
                            }, null
                        );

                        try
                        {
                            if (gameDb)
                            {
                                newGameContext = new GameContext(
                                    connectionStringBuilder, DatabaseOptions.DatabaseType.MySQL
                                );

                                if (newGameContext.IsEmpty())
                                {
                                    newGameContext.Database.EnsureDeleted();
                                    newGameContext.Database.Migrate();
                                }
                                else
                                {
                                    Console.WriteLine(Strings.Migration.mysqlnotempty);

                                    return;
                                }
                            }
                            else
                            {
                                newPlayerContext = new PlayerContext(
                                    connectionStringBuilder, DatabaseOptions.DatabaseType.MySQL
                                );

                                if (newPlayerContext.IsEmpty())
                                {
                                    newPlayerContext.Database.EnsureDeleted();
                                    newPlayerContext.Database.Migrate();
                                }
                                else
                                {
                                    Console.WriteLine(Strings.Migration.mysqlnotempty);

                                    return;
                                }
                            }

                            newOpts.Type = DatabaseOptions.DatabaseType.mysql;
                            newOpts.Server = host;
                            newOpts.Port = port;
                            newOpts.Database = database;
                            newOpts.Username = user;
                            newOpts.Password = pass;

                            dbConnected = true;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(Strings.Migration.mysqlconnectionerror.ToString(ex));
                            Console.WriteLine();
                            Console.WriteLine(Strings.Migration.mysqltryagain);
                            var input = Console.ReadLine();
                            var key = input.Length > 0 ? input[0] : ' ';
                            Console.WriteLine();
                            if (!string.Equals(
                                Strings.Migration.tryagaincharacter, key.ToString(), StringComparison.Ordinal
                            ))
                            {
                                Console.WriteLine(Strings.Migration.migrationcancelled);

                                return;
                            }
                        }
                    }

                    break;

                case DatabaseOptions.DatabaseType.SQLite:
                    //If the file exists make sure it is safe to delete
                    var dbExists = gameDb && File.Exists(GameDbFilename) || !gameDb && File.Exists(PlayersDbFilename);
                    if (dbExists)
                    {
                        Console.WriteLine();
                        var filename = gameDb ? GameDbFilename : PlayersDbFilename;
                        Console.WriteLine(Strings.Migration.sqlitealreadyexists.ToString(filename));
                        var input = Console.ReadLine();
                        var key = input.Length > 0 ? input[0] : ' ';
                        Console.WriteLine();
                        if (key.ToString() != Strings.Migration.overwritecharacter)
                        {
                            Console.WriteLine(Strings.Migration.migrationcancelled);

                            return;
                        }
                    }

                    if (gameDb)
                    {
                        newGameContext = new GameContext(
                            CreateConnectionStringBuilder(
                                new DatabaseOptions
                                {
                                    Type = DatabaseOptions.DatabaseType.SQLite
                                }, GameDbFilename
                            ), DatabaseOptions.DatabaseType.SQLite
                        );

                        newGameContext.Database.EnsureDeleted();
                        newGameContext.Database.Migrate();
                    }
                    else
                    {
                        newPlayerContext = new PlayerContext(
                            CreateConnectionStringBuilder(
                                new DatabaseOptions
                                {
                                    Type = DatabaseOptions.DatabaseType.SQLite
                                }, PlayersDbFilename
                            ), DatabaseOptions.DatabaseType.SQLite
                        );

                        newPlayerContext.Database.EnsureDeleted();
                        newPlayerContext.Database.Migrate();
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(convType));
            }

            //Shut down server, start migration.
            Console.WriteLine(Strings.Migration.stoppingserver);

            //This variable will end the server loop and save any pending changes
            ServerContext.Instance.RequestShutdown();

            while (ServerContext.Instance.IsRunning)
            {
                System.Threading.Thread.Sleep(100);
            }


            Console.WriteLine(Strings.Migration.startingmigration);
            if (gameDb && newGameContext != null)
            {
                using (var context = CreateGameContext(readOnly: false))
                {
                    MigrateDbSet(context.Animations, newGameContext.Animations);
                    MigrateDbSet(context.Classes, newGameContext.Classes);
                    MigrateDbSet(context.CraftingTables, newGameContext.CraftingTables);
                    MigrateDbSet(context.Crafts, newGameContext.Crafts);
                    MigrateDbSet(context.Events, newGameContext.Events);
                    MigrateDbSet(context.Items, newGameContext.Items);
                    //MigrateDbSet(context.MapFolders, newGameContext.MapFolders);
                    MigrateDbSet(context.Maps, newGameContext.Maps);
                    MigrateDbSet(context.Npcs, newGameContext.Npcs);
                    MigrateDbSet(context.Projectiles, newGameContext.Projectiles);
                    MigrateDbSet(context.Quests, newGameContext.Quests);
                    MigrateDbSet(context.Resources, newGameContext.Resources);
                    MigrateDbSet(context.Shops, newGameContext.Shops);
                    MigrateDbSet(context.Spells, newGameContext.Spells);
                    MigrateDbSet(context.ServerVariables, newGameContext.ServerVariables);
                    MigrateDbSet(context.PlayerVariables, newGameContext.PlayerVariables);
                    MigrateDbSet(context.Tilesets, newGameContext.Tilesets);
                    MigrateDbSet(context.Time, newGameContext.Time);
                    newGameContext.ChangeTracker.DetectChanges();
                    newGameContext.SaveChanges();
                    newGameContext.Dispose();
                    newGameContext = null;
                }
                Options.GameDb = newOpts;
                Options.SaveToDisk();
            }
            else if (!gameDb && newPlayerContext != null)
            {
                using (var context = CreatePlayerContext(readOnly: false))
                {
                    MigrateDbSet(context.Users, newPlayerContext.Users);
                    MigrateDbSet(context.Players, newPlayerContext.Players);
                    MigrateDbSet(context.Player_Friends, newPlayerContext.Player_Friends);
                    MigrateDbSet(context.Player_Spells, newPlayerContext.Player_Spells);
                    MigrateDbSet(context.Player_Variables, newPlayerContext.Player_Variables);
                    MigrateDbSet(context.Player_Hotbar, newPlayerContext.Player_Hotbar);
                    MigrateDbSet(context.Player_Quests, newPlayerContext.Player_Quests);
                    MigrateDbSet(context.Bags, newPlayerContext.Bags);
                    MigrateDbSet(context.Player_Items, newPlayerContext.Player_Items);
                    MigrateDbSet(context.Player_Bank, newPlayerContext.Player_Bank);
                    MigrateDbSet(context.Bag_Items, newPlayerContext.Bag_Items);
                    MigrateDbSet(context.Mutes, newPlayerContext.Mutes);
                    MigrateDbSet(context.Bans, newPlayerContext.Bans);
                    newPlayerContext.ChangeTracker.DetectChanges();
                    newPlayerContext.SaveChanges();
                    newPlayerContext.Dispose();
                    newPlayerContext = null;
                }
                Options.PlayerDb = newOpts;
                Options.SaveToDisk();

            }

            Console.WriteLine(Strings.Migration.migrationcomplete);
            Bootstrapper.Context.ConsoleService.Wait(true);
            Environment.Exit(0);
        }

        private static void MigrateDbSet<T>(DbSet<T> oldDbSet, DbSet<T> newDbSet) where T : class
        {
            foreach (var itm in oldDbSet)
            {
                newDbSet.Add(itm);
            }
        }

        //Code taken from Stackoverflow on 9/20/2018
        //Answer by Dai and Damian Leszczyński - Vash
        //https://stackoverflow.com/questions/3404421/password-masking-console-application
        public static string GetPassword()
        {
            var pwd = "";
            while (true)
            {
                var i = Console.ReadKey(true);
                if (i.Key == ConsoleKey.Enter)
                {
                    break;
                }
                else if (i.Key == ConsoleKey.Backspace)
                {
                    if (pwd.Length > 1)
                    {
                        pwd = pwd.Remove(pwd.Length - 2, 1);
                        Console.Write("\b \b");
                    }
                }
                else if (i.KeyChar != '\u0000'
                ) // KeyChar == '\u0000' if the key pressed does not correspond to a printable character, e.g. F1, Pause-Break, etc
                {
                    pwd = pwd + i.KeyChar;
                    Console.Write("*");
                }
            }

            return pwd;
        }

    }

}
