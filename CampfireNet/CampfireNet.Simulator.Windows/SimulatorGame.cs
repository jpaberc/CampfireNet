﻿using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampfireNet.Utilities;
using CampfireNet.Utilities.AsyncPrimatives;
using CampfireNet.Utilities.ChannelsExtensions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using static CampfireNet.Utilities.ChannelsExtensions.ChannelsExtensions;

namespace CampfireNet.Simulator {
   public struct SimulationBluetoothConnectionState {
      public float Quality;
      public float Connectedness;
   }

   public class SimulationBluetoothState {
      public SimulationBluetoothConnectionState[] ConnectionStates { get; set; }
   }
   
   public class DeviceAgent {
      public Guid BluetoothAdapterId;
      public Vector2 Position;
      public Vector2 Velocity;
      public SimulationBluetoothState BluetoothState;
      public SimulationBluetoothAdapter BluetoothAdapter;
      public CampfireNetClient Client;
   }

   public interface IBluetoothNeighbor {
      bool IsConnected { get; }
      ReadableChannel<byte[]> InboundChannel { get; }
      Task<bool> TryHandshakeAsync();
      Task TrySendAsync(byte[] data);
   }

   public interface IBluetoothAdapter {
      Task<IReadOnlyList<IBluetoothNeighbor>> DiscoverAsync();
   }

   public class NotConnectedException : Exception {

   }

   public class SimulationBluetoothAdapter : IBluetoothAdapter {
      private readonly AsyncSemaphore requestRateLimitSemaphore = new AsyncSemaphore(0);
      private readonly DeviceAgent[] agents;
      private readonly int agentIndex;
      private readonly SimulationBluetoothState bluetoothState;
      public const int MAX_RATE_LIMIT_TOKENS = 3;
      private float rateLimitTokenGrantingCounter = 0.0f;
      private readonly Dictionary<Guid, SimulationBluetoothNeighbor> neighborsByAdapterId;

      public Dictionary<Guid, SimulationBluetoothNeighbor> NeighborsByAdapterId => neighborsByAdapterId;

      public SimulationBluetoothAdapter(DeviceAgent[] agents, int agentIndex, Dictionary<Guid, SimulationBluetoothNeighbor> neighborsByAdapterId) {
         this.agents = agents;
         this.agentIndex = agentIndex;
         this.bluetoothState = agents[agentIndex].BluetoothState;
         this.neighborsByAdapterId = neighborsByAdapterId;
      }

      public void Permit(float dt) {
         rateLimitTokenGrantingCounter += dt;

         if (rateLimitTokenGrantingCounter > 0.100f) {
            rateLimitTokenGrantingCounter -= 1.0f;

            if (requestRateLimitSemaphore.Count < MAX_RATE_LIMIT_TOKENS) {
               requestRateLimitSemaphore.Release();
            }
         }
      }

      public async Task<IReadOnlyList<IBluetoothNeighbor>> DiscoverAsync() {
         await requestRateLimitSemaphore.WaitAsync();
         var neighbors = new List<IBluetoothNeighbor>();
         for (int i = 0; i < agents.Length; i++) {
            if (bluetoothState.ConnectionStates[i].Connectedness == 1.0f) {
               neighbors.Add(neighborsByAdapterId[agents[i].BluetoothAdapterId]);
            }
         }
         return neighbors;
      }

      public void Inject(Guid senderId, byte[] contents) {

      }

      public class SimulationBluetoothNeighbor : IBluetoothNeighbor {
         private readonly DeviceAgent self;
         private readonly SimulationConnectionContext connectionContext;

         public SimulationBluetoothNeighbor(DeviceAgent self, SimulationConnectionContext connectionContext) {
            this.self = self;
            this.connectionContext = connectionContext;
         }

         public async Task<bool> TryHandshakeAsync() {
            try {
               await HandshakeAsync();
               return true;
            } catch (TimeoutException) {
               return false;
            }
         }

         public Task HandshakeAsync() {
            return connectionContext.ConnectAsync(self);
         }

         public Task TrySendAsync(byte[] data) {
            return connectionContext.SendAsync(self, data);
         }

         public bool IsConnected => connectionContext.IsAppearingConnected(self);
         public ReadableChannel<byte[]> InboundChannel => connectionContext.GetInboundChannel(self);
         public WritableChannel<byte[]> OutboundChannel => connectionContext.GetOtherInboundChannel(self);
      }

      public class AsyncPriorityQueue<T> {
         private readonly AsyncLock sync = new AsyncLock();
         private readonly PriorityQueue<T> inner;

         public AsyncPriorityQueue(PriorityQueue<T> inner) {
            this.inner = inner;
         }

         public int Count => inner.Count;

         public async Task EnqueueAsync(T item) {
            using (await sync.LockAsync()) {
               inner.Enqueue(item);
            }
         }

         public async Task<(bool success, T item)> TryDequeueAsync() {
            using (await sync.LockAsync()) {
               if (inner.IsEmpty) {
                  return (false, default(T));
               } else {
                  return (true, inner.Dequeue());
               }
            }
         }
      }

      public class AsyncAdapterEventPriorityQueueChannel<T> : Channel<T> {
         private readonly object queueSync = new object();
         private readonly PriorityQueue<T> queue;
         private readonly Channel<bool> available;
         private readonly Func<T, DateTime> getItemAvailableTime;

         public AsyncAdapterEventPriorityQueueChannel(PriorityQueue<T> queue, Channel<bool> available, Func<T, DateTime> getItemAvailableTime) {
            this.queue = queue;
            this.available = available;
            this.getItemAvailableTime = getItemAvailableTime;
         }

         public int Count => queue.Count;

         public bool TryRead(out T message) {
            bool throwaway;
            if (available.TryRead(out throwaway)) {
               lock (queueSync) {
                  message = queue.Dequeue();
               }
               return true;
            }
            message = default(T);
            return false;
         }

         public async Task<T> ReadAsync(CancellationToken cancellationToken, Func<T, bool> acceptanceTest) {
            while (true) {
               await available.ReadAsync(cancellationToken, x => true);
               lock (queueSync) {
                  if (acceptanceTest(queue.Peek())) {
                     return queue.Dequeue();
                  }
               }
               await available.WriteAsync(true, CancellationToken.None);
            }
         }

         public async Task WriteAsync(T message, CancellationToken cancellationToken) {
            lock (queueSync) {
               queue.Enqueue(message);
            }
            Go(async () => {
               var now = DateTime.Now;
               var ready = getItemAvailableTime(message);
               if (now < ready) {
                  await Task.Delay(ready - now);
               }
               await available.WriteAsync(true, CancellationToken.None);
            }).Forget();
         }
      }

      public class SimulationConnectionContext {
         private readonly AsyncLock synchronization = new AsyncLock();
         private readonly DeviceAgent firstAgent; 
         private readonly DeviceAgent secondAgent;
         private readonly Channel<AdapterEvent> adapterEventQueueChannel = new AsyncAdapterEventPriorityQueueChannel<AdapterEvent>(
            new PriorityQueue<AdapterEvent>((a, b) => a.Time.CompareTo(b.Time)),
            ChannelFactory.Nonblocking<bool>(),
            item => item.Time);

         // state
         private bool isFirstConnected = false;
         private bool isSecondConnected = false;

         // connect: 
         private bool isConnectingPeerPending;
         private AsyncLatch connectingPeerSignal;

         // send:
         private readonly Channel<byte[]> firstInboundChannel = ChannelFactory.Nonblocking<byte[]>();
         private readonly Channel<byte[]> secondInboundChannel = ChannelFactory.Nonblocking<byte[]>();

         public SimulationConnectionContext(DeviceAgent firstAgent, DeviceAgent secondAgent) {
            this.firstAgent = firstAgent;
            this.secondAgent = secondAgent;
         }

         public void Start() {
            RunAsync().Forget();
         }

         private async Task RunAsync() {
            var pendingBeginConnect = (BeginConnectEvent)null;

            while (true) {
               var adapterEvent = await adapterEventQueueChannel.ReadAsync(CancellationToken.None, x => true);
               switch (adapterEvent.GetType().Name) {
                  case nameof(BeginConnectEvent):
                     var beginConnect = (BeginConnectEvent)adapterEvent;
                     if (pendingBeginConnect == null) {
                        pendingBeginConnect = beginConnect;
                     } else {
                        //                              Console.WriteLine("Connect success!");
                        pendingBeginConnect.ResultBox.SetResult(true);
                        beginConnect.ResultBox.SetResult(true);

                        pendingBeginConnect = null;
                        isFirstConnected = true;
                        isSecondConnected = true;
                     }
                     break;
                  case nameof(TimeoutConnectEvent):
                     var timeout = (TimeoutConnectEvent)adapterEvent;
                     if (timeout.BeginEvent == pendingBeginConnect) {
                        pendingBeginConnect.ResultBox.SetException(new TimeoutException());
                        pendingBeginConnect = null;
                     }
                     break;
                  case nameof(SendEvent):
                     var send = (SendEvent)adapterEvent;
                     if (!GetIsConnected(send.Initiator)) {
                        send.CompletionBox.SetException(new NotConnectedException());
                        break;
                     }

                     var connectivity = SimulationBluetoothCalculator.ComputeConnectivity(firstAgent, secondAgent);
                     if (!connectivity.InRange) {
                        SetIsConnected(GetOther(send.Initiator), false);
                        SetIsConnected(send.Initiator, false);
                        send.CompletionBox.SetException(new NotConnectedException());
                        break;
                     }

                     var deltaBytesSent = (int)Math.Ceiling(connectivity.SignalQuality * send.Interval.TotalSeconds * SimulationBluetoothConstants.MAX_OUTBOUND_BYTES_PER_SECOND);
                     var bytesSent = send.BytesSent + deltaBytesSent;
                     if (bytesSent >= send.Payload.Length) {
                        await GetOtherInboundChannel(send.Initiator).WriteAsync(send.Payload);
                        send.CompletionBox.SetResult(true);
                        break;
                     }

                     var nextEvent = new SendEvent(DateTime.Now + send.Interval, send.Interval, send.Initiator, send.CompletionBox, send.Payload, bytesSent);
                     await adapterEventQueueChannel.WriteAsync(nextEvent, CancellationToken.None);
                     break;
               }
            }
         }

         public async Task ConnectAsync(DeviceAgent sender) {
            var now = DateTime.Now;
            var connectEvent = new BeginConnectEvent(now + TimeSpan.FromMilliseconds(SimulationBluetoothConstants.BASE_HANDSHAKE_DELAY_MILLIS), sender);
            await adapterEventQueueChannel.WriteAsync(connectEvent);
            var timeoutEvent = new TimeoutConnectEvent(now + TimeSpan.FromMilliseconds(SimulationBluetoothConstants.HANDSHAKE_TIMEOUT_MILLIS), connectEvent);
            await adapterEventQueueChannel.WriteAsync(timeoutEvent);
            await connectEvent.ResultBox.GetResultAsync();
         }

         public async Task SendAsync(DeviceAgent sender, byte[] contents) {
            var interval = TimeSpan.FromMilliseconds(SimulationBluetoothConstants.SEND_TICK_INTERVAL);
            var completionBox = new AsyncBox<bool>();
            var sendEvent = new SendEvent(DateTime.Now + interval, interval, sender, completionBox, contents, 0);
            await adapterEventQueueChannel.WriteAsync(sendEvent);
            await completionBox.GetResultAsync();
         }

         private DeviceAgent GetOther(DeviceAgent self) => self == firstAgent ? secondAgent : firstAgent;
         public Channel<byte[]> GetInboundChannel(DeviceAgent self) => self == firstAgent ? firstInboundChannel : secondInboundChannel;
         public Channel<byte[]> GetOtherInboundChannel(DeviceAgent self) => GetInboundChannel(GetOther(self));
         public bool GetIsConnected(DeviceAgent self) => self == firstAgent ? isFirstConnected : isSecondConnected;
         public void SetIsConnected(DeviceAgent self, bool value) {
            if (self == firstAgent) {
               isFirstConnected = value;
            } else {
               isSecondConnected = value;
            }
         }

         private async Task AssertConnectedElseTimeout(DeviceAgent sender) {
            if (sender == firstAgent) {
               if (!isFirstConnected) {
                  throw new InvalidOperationException("not connected");
               } else if (!SimulationBluetoothCalculator.ComputeConnectivity(firstAgent, secondAgent).IsSufficientQuality) {
                  await Task.Delay(SimulationBluetoothConstants.SENDRECV_TIMEOUT_MILLIS);
                  isFirstConnected = false;
                  throw new TimeoutException();
               }
            } else {
               if (!isSecondConnected) {
                  throw new InvalidOperationException("not connected");
               } else if (!SimulationBluetoothCalculator.ComputeConnectivity(firstAgent, secondAgent).IsSufficientQuality) {
                  await Task.Delay(SimulationBluetoothConstants.SENDRECV_TIMEOUT_MILLIS);
                  isSecondConnected = false;
                  throw new TimeoutException();
               }
            }
         }

         public bool IsAppearingConnected(DeviceAgent self) {
            return self == firstAgent ? isFirstConnected : isSecondConnected;
         }
      }

      public abstract class AdapterEvent {
         protected AdapterEvent(DateTime time) {
            Time = time;
         }

         public DateTime Time { get; }
      }

      public class BeginConnectEvent : AdapterEvent {
         public BeginConnectEvent(DateTime time, DeviceAgent initiator) : base(time) {
            Initiator = initiator;
         }

         public DeviceAgent Initiator { get; }
         public AsyncBox<bool> ResultBox { get; } = new AsyncBox<bool>();
      }

      public class TimeoutConnectEvent : AdapterEvent {
         public TimeoutConnectEvent(DateTime time, BeginConnectEvent beginEvent) : base(time) {
            BeginEvent = beginEvent;
         }

         public BeginConnectEvent BeginEvent { get; }
      }

      public class SendEvent : AdapterEvent {
         public SendEvent(DateTime time, TimeSpan interval, DeviceAgent initiator, AsyncBox<bool> completionBox, byte[] payload, int bytesSent) : base(time) {
            Interval = interval;
            Initiator = initiator;
            CompletionBox = completionBox;
            Payload = payload;
            BytesSent = bytesSent;
         }

         public TimeSpan Interval { get; }
         public DeviceAgent Initiator { get; }
         public AsyncBox<bool> CompletionBox { get; }
         public byte[] Payload { get; }
         public int BytesSent { get; }
      }
   }

   public static class SimulationBluetoothConstants {
      public const int RANGE = 100;
      public const int RANGE_SQUARED = RANGE * RANGE;

      public const int BASE_HANDSHAKE_DELAY_MILLIS = 300;
      public const float MIN_VIABLE_SIGNAL_QUALITY = 0.2f;

      public const int HANDSHAKE_TIMEOUT_MILLIS = 5000;
      public const int SENDRECV_TIMEOUT_MILLIS = 2000;
      public const int SEND_TICK_INTERVAL = 300;

      public const int MAX_OUTBOUND_BYTES_PER_SECOND = 3 * 1024 * 1024;
   }

   public static class SimulationBluetoothCalculator {
      public static SimulationBluetoothConnectivity ComputeConnectivity(DeviceAgent a, DeviceAgent b) {
         var quality = 1.0 - (a.Position - b.Position).LengthSquared() / SimulationBluetoothConstants.RANGE_SQUARED;
         return new SimulationBluetoothConnectivity {
            InRange = quality > 0.0,
            IsSufficientQuality = quality > SimulationBluetoothConstants.MIN_VIABLE_SIGNAL_QUALITY,
            SignalQuality = (float)Math.Max(0, quality)
         };
      }

      public static TimeSpan ComputeHandshakeDelay(float connectivitySignalQuality) {
         return TimeSpan.FromMilliseconds((int)(SimulationBluetoothConstants.BASE_HANDSHAKE_DELAY_MILLIS / connectivitySignalQuality));
      }
   }

   public class SimulationBluetoothConnectivity {
      public bool InRange { get; set; }
      public bool IsSufficientQuality { get; set; }
      public float SignalQuality { get; set; }
   }

   public class SimulatorGame : Game {
      private readonly SimulatorConfiguration configuration;
      private readonly DeviceAgent[] agents;
      private readonly GraphicsDeviceManager graphicsDeviceManager;

      private SpriteBatch spriteBatch;
      private Texture2D whiteTexture;
      private Texture2D whiteCircleTexture;
      private RasterizerState rasterizerState;
      private int epoch = 0;

      public SimulatorGame(SimulatorConfiguration configuration, DeviceAgent[] agents) {
         this.configuration = configuration;
         this.agents = agents;

         graphicsDeviceManager = new GraphicsDeviceManager(this) {
            PreferredBackBufferWidth = configuration.DisplayWidth,
            PreferredBackBufferHeight = configuration.DisplayHeight,
            PreferMultiSampling = true
         };
      }

      protected override void LoadContent() {
         base.LoadContent();

         spriteBatch = new SpriteBatch(graphicsDeviceManager.GraphicsDevice);
         SpriteBatchEx.GraphicsDevice = GraphicsDevice;

         whiteTexture = CreateSolidTexture(Color.White);
         whiteCircleTexture = CreateSolidCircleTexture(Color.White, 256);

         rasterizerState = GraphicsDevice.RasterizerState = new RasterizerState { MultiSampleAntiAlias = true };

         epoch++;
         agents[0].Client.Number = epoch;
         //for (int i = 0; i < agents.Count; i++) {
         //   agents[i].Position = new Vector2(320 + 50 * (i % 14), 80 + 70 * i / 14);
         //   agents[i].Velocity *= 0.05f;
         //}
         //agents[36].Value = MAX_VALUE;
      }

      protected override void Update(GameTime gameTime) {
         base.Update(gameTime);
         var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

         if (Keyboard.GetState().IsKeyDown(Keys.S)) {
            dt *= 10;
         }

         for (var i = 0; i < agents.Length; i++) {
            var agent = agents[i];
            agent.Position += agent.Velocity * dt;
            if (agent.Position.X < configuration.AgentRadius)
               agent.Velocity.X = Math.Abs(agent.Velocity.X);
            if (agent.Position.X > configuration.FieldWidth - configuration.AgentRadius)
               agent.Velocity.X = -Math.Abs(agent.Velocity.X);
            if (agent.Position.Y < configuration.AgentRadius)
               agent.Velocity.Y = Math.Abs(agent.Velocity.Y);
            if (agent.Position.Y > configuration.FieldHeight - configuration.AgentRadius)
               agent.Velocity.Y = -Math.Abs(agent.Velocity.Y);
            agent.BluetoothAdapter.Permit(dt);
         }

         var dConnectnessInRangeBase = dt * 5.0f;
         var dConnectnessOutOfRangeBase = -dt * 50.0f;
         for (var i = 0; i < agents.Length - 1; i++) {
            var a = agents[i];
            var aConnectionStates = a.BluetoothState.ConnectionStates;
            for (var j = i + 1; j < agents.Length; j++) {
               var b = agents[j];
               var bConnectionStates = b.BluetoothState.ConnectionStates;
               var distanceSquared = (a.Position - b.Position).LengthSquared();
               var quality = 1.0f - distanceSquared / (float)SimulationBluetoothConstants.RANGE_SQUARED; // Math.Max(0.0f, 1.0f - distanceSquared / (float)BLUETOOTH_RANGE_SQUARED);
               var inRange = distanceSquared < SimulationBluetoothConstants.RANGE_SQUARED;
               float connectedness = aConnectionStates[j].Connectedness;
               var dConnectedness = (inRange ? quality * dConnectnessInRangeBase : dConnectnessOutOfRangeBase);
               connectedness = Math.Max(0.0f, Math.Min(1.0f, connectedness + dConnectedness));

               aConnectionStates[j].Quality = bConnectionStates[i].Quality = quality;
               aConnectionStates[j].Connectedness = bConnectionStates[i].Connectedness = connectedness;
            }
         }

         if (Keyboard.GetState().IsKeyDown(Keys.A)) {
            epoch++;
            agents[(int)(DateTime.Now.ToFileTime() % agents.Length)].Client.Number = epoch;
         }
      }

      protected override void Draw(GameTime gameTime) {
         base.Draw(gameTime);

         GraphicsDevice.Clear(Color.White);
         spriteBatch.Begin(SpriteSortMode.Deferred, null, transformMatrix: Matrix.CreateScale((float)configuration.DisplayHeight / configuration.FieldHeight));

         for (var i = 0; i < agents.Length - 1; i++) {
            var a = agents[i];
            for (var j = i + 1; j < agents.Length; j++) {
               var b = agents[j];
               var neighborBluetoothAdapter = a.BluetoothAdapter.NeighborsByAdapterId[b.BluetoothAdapterId];
               if (neighborBluetoothAdapter.IsConnected)
                  spriteBatch.DrawLine(a.Position, b.Position, Color.Gray);
            }
         }

         for (var i = 0; i < agents.Length; i++) {
            DrawCenteredCircleWorld(agents[i].Position, configuration.AgentRadius, agents[i].Client.Number != epoch ? Color.Gray : Color.Red);
         }
         //spriteBatch.DrawLine(new Vector2(0, 50), new Vector2(100, 50), Color.Red);
         spriteBatch.End();
      }

      private Texture2D CreateSolidTexture(Color color) {
         var texture = new Texture2D(GraphicsDevice, 1, 1);
         texture.SetData(new[] { color });
         return texture;
      }

      private Texture2D CreateSolidCircleTexture(Color color, int radius) {
         var diameter = radius * 2;

         // could optimize by symmetry, but whatever this is cheap
         var imageData = new Color[diameter * diameter];
         for (int x = 0; x < diameter; x++) {
            for (int y = 0; y < diameter; y++) {
               imageData[x * diameter + y] = new Vector2(x - radius, y - radius).Length() <= radius ? color : Color.Transparent;
            }
         }

         var texture = new Texture2D(GraphicsDevice, diameter, diameter);
         texture.SetData(imageData);
         return texture;
      }

      public void DrawCenteredCircleWorld(Vector2 center, float radius, Color color) {
         spriteBatch.Draw(
            whiteCircleTexture,
            new Rectangle((int)(center.X - radius), (int)(center.Y - radius), (int)(2 * radius), (int)(2 * radius)),
            color
         );
      }
   }
}
