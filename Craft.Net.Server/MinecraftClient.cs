using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Craft.Net;
using Craft.Net.Data;
using Craft.Net.Data.Entities;
using System.Diagnostics;
using System.Globalization;
using Craft.Net.Data.Blocks;

namespace Craft.Net.Server
{
    /// <summary>
    /// Describes a client connected to a <see cref="MinecraftServer"/>.
    /// </summary>
    public class MinecraftClient
    {
        public int Reach { get { return Entity.GameMode == GameMode.Creative ? 6 : 5; }}

        #region Fields

        /// <summary>
        /// True if the client has enabled colors in chat.
        /// </summary>
        public bool ColorsEnabled;
        /// <summary>
        /// The entity this client represents.
        /// </summary>
        public PlayerEntity Entity;
        /// <summary>
        /// The speed at which the client is permitted to fly.
        /// </summary>
        public byte FlyingSpeed;
        /// <summary>
        /// The hostname the client connected to.
        /// </summary>
        public string Hostname;
        /// <summary>
        /// Set to true if the client has completed the login sequence and
        /// has been spawned.
        /// </summary>
        public bool IsLoggedIn;
        /// <summary>
        /// A list of all chunks the client has been sent and
        /// instructed to load.
        /// </summary>
        public List<Vector3> LoadedChunks;
        /// <summary>
        /// The client-provided locale string.
        /// </summary>
        public CultureInfo Locale;
        /// <summary>
        /// The view distance in chunks.
        /// </summary>
        public int MaxViewDistance;
        /// <summary>
        /// The time, in milliseconds, it takes this client to respond to a
        /// <see cref="KeepAlivePacket"/>.
        /// </summary>
        public short Ping;
        /// <summary>
        /// The current queue of packets to be sent to this client.
        /// </summary>
        public ConcurrentQueue<IPacket> SendQueue;
        /// <summary>
        /// The <see cref="MinecraftServer"/> managing this client's connection.
        /// </summary>
        public MinecraftServer Server;
        /// <summary>
        /// 3rd party client-specific data may be saved here.
        /// </summary>
        public Dictionary<string, object> Tags;
        /// <summary>
        /// This client's username.
        /// </summary>
        public string Username;
        /// <summary>
        /// The view distance in chunks.
        /// </summary>
        public int ViewDistance;
        /// <summary>
        /// The speed at which this client is permitted to walk.
        /// </summary>
        public byte WalkingSpeed;
        internal DateTime ExpectedMiningEnd;
        internal Vector3 ExpectedBlockToMine;
        /// <summary>
        /// Plugin channels this client has requested to listen to.
        /// </summary>
        public List<string> PluginChannels { get; set; }

        public NetworkStream NetworkStream { get; set; }
        public MinecraftStream Stream { get; set; }

        public World World
        {
            get { return Server.EntityManager.GetEntityWorld(Entity); }
        }

        internal List<int> KnownEntities;
        internal string AuthenticationHash;
        internal Timer KeepAliveTimer, UpdateLoadedChunksTimer;
        internal DateTime LastKeepAlive, LastKeepAliveSent;
        internal bool EncryptionEnabled;
        internal byte[] SharedKey;
        internal TcpClient TcpClient;
        internal bool DisconnectPending;

        #endregion

        /// <summary>
        /// Creates a new MinecraftClient with the specified socket to be
        /// managed by the given <see cref="MinecraftServer"/>.
        /// </summary>
        public MinecraftClient(TcpClient client, MinecraftServer server)
        {
            TcpClient = client;
            NetworkStream = client.GetStream();
            Stream = new MinecraftStream(new BufferedStream(client.GetStream()));
            SendQueue = new ConcurrentQueue<IPacket>();
            IsLoggedIn = false;
            EncryptionEnabled = false;
            Locale = CultureInfo.CurrentCulture;
            MaxViewDistance = 10;
            ViewDistance = 3;
            LoadedChunks = new List<Vector3>();
            Server = server;
            WalkingSpeed = 12;
            FlyingSpeed = 25;
            LastKeepAlive = DateTime.MaxValue.AddSeconds(-10);
            KnownEntities = new List<int>();
            PluginChannels = new List<string>();
            Tags = new Dictionary<string, object>();
            DisconnectPending = false;
        }

        /// <summary>
        /// The maximum speed that a client may move.
        /// </summary>
        public double MaxMoveDistance
        {
            get
            {
                // TODO: Base this on speed
                return 1000;
            }
        }

        /// <summary>
        /// Queues the given packet for sending. Make sure to call
        /// <see cref="MinecraftServer.ProcessSendQueue"/> to send
        /// the queued packet.
        /// </summary>
        /// <param name="packet"></param>
        public virtual void SendPacket(IPacket packet)
        {
            if (packet == null)
                return;
            if (packet is DisconnectPacket)
                DisconnectPending = true;
            SendQueue.Enqueue(packet);
        }

        /// <summary>
        /// Asyncronously updates chunks loaded on the client
        /// </summary>
        /// <returns></returns>
        public Task UpdateChunksAsync()
        {
            if ((int)(Entity.Position.X) >> 4 != (int)(Entity.OldPosition.X) >> 4 ||
                (int)(Entity.Position.Z) >> 4 != (int)(Entity.OldPosition.Z) >> 4)
            {
                return Task.Factory.StartNew(() => UpdateChunks(true));
            }
            return null;
        }

        /// <summary>
        /// Asyncronously updates chunks loaded on the client and forces a
        /// recalculation of which chunks should be loaded.
        /// </summary>
        public Task ForceUpdateChunksAsync()
        {
            return Task.Factory.StartNew(() => UpdateChunks(true));
        }

        /// <summary>
        /// Updates which chunks are loaded on the client.
        /// </summary>
        public virtual void UpdateChunks(bool forceUpdate)
        {
            if (forceUpdate ||
                (int)(Entity.Position.X) >> 4 != (int)(Entity.OldPosition.X) >> 4 ||
                (int)(Entity.Position.Z) >> 4 != (int)(Entity.OldPosition.Z) >> 4
                )
            {
                var newChunks = new List<Vector3>();
                for (int x = -ViewDistance; x < ViewDistance; x++)
                    for (int z = -ViewDistance; z < ViewDistance; z++)
                    {
                        newChunks.Add(new Vector3(
                                          ((int)Entity.Position.X >> 4) + x,
                                          0,
                                          ((int)Entity.Position.Z >> 4) + z));
                    }
                // Unload extraneous columns
                var currentChunks = new List<Vector3>(LoadedChunks);
                foreach (Vector3 chunk in currentChunks)
                {
                    if (!newChunks.Contains(chunk))
                        UnloadChunk(chunk);
                }
                // Load new columns
                foreach (Vector3 chunk in newChunks)
                {
                    if (!LoadedChunks.Contains(chunk))
                        LoadChunk(chunk);
                }
            }
        }

        /// <summary>
        /// Loads the given chunk on the client.
        /// </summary>
        public virtual void LoadChunk(Vector3 position)
        {
            World world = Server.EntityManager.GetEntityWorld(Entity);
            Chunk chunk = world.GetChunk(position);
            SendPacket(ChunkHelper.CreatePacket(chunk));
            if (chunk.TileEntities.Count != 0)
            {
                foreach (var tileEntity in chunk.TileEntities)
                {
                    Console.WriteLine("Handling tile entity: " + tileEntity.Value.GetType().Name);
                    if (tileEntity.Value is SignTileEntity)
                    {
                        var signData = tileEntity.Value as SignTileEntity;
                        SendPacket(new UpdateSignPacket((int)tileEntity.Key.X, (short)tileEntity.Key.Y, (int)tileEntity.Key.Z,
                            signData.Text1, signData.Text2, signData.Text3, signData.Text4));
                    }
                }
            }
            this.LoadedChunks.Add(position);
        }

        /// <summary>
        /// Unloads the given chunk on the client.
        /// </summary>
        public virtual void UnloadChunk(Vector3 position)
        {
            var dataPacket = new ChunkDataPacket();
            dataPacket.AddBitMap = 0;
            dataPacket.GroundUpContinuous = true;
            dataPacket.PrimaryBitMap = 0;
            dataPacket.X = (int)position.X;
            dataPacket.Z = (int)position.Z;
            dataPacket.Data = ChunkHelper.ChunkRemovalSequence;
            SendPacket(dataPacket);
            this.LoadedChunks.Remove(position);
        }

        /// <summary>
        /// Sends a <see cref="ChatMessagePacket"/> to the client.
        /// </summary>
        public virtual void SendChat(string message)
        {
            SendPacket(new ChatMessagePacket(message));
        }

        public void DelaySendPacket(IPacket packet, int milliseconds)
        {
            new Timer((discarded) => SendPacket(packet), null, milliseconds, Timeout.Infinite);
        }

        internal void StartWorkers()
        {
            KeepAliveTimer = new Timer(KeepAlive, null, 1000, 5000);
            UpdateLoadedChunksTimer = new Timer(UpdateLoadedChunks, null, 1000, 1000);
        }

        protected internal virtual void KeepAlive(object discarded)
        {
            if (LastKeepAlive.AddSeconds(10) < DateTime.Now && false) // TODO
                LogProvider.Log("Client timed out");
            else
            {
                SendPacket(new KeepAlivePacket(MathHelper.Random.Next()));
                LastKeepAliveSent = DateTime.Now;
            }
        }

        internal void UpdateLoadedChunks(object discarded)
        {
            if (ViewDistance < MaxViewDistance)
            {
                ViewDistance++;
                ForceUpdateChunksAsync(); // TODO: Move this to its own timer
            }
        }
    }
}