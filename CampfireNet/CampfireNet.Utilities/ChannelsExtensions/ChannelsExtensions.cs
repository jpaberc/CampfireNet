﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CampfireNet.Utilities.AsyncPrimatives;
using static CampfireNet.Utilities.ChannelsExtensions.ToFuncTTaskConverter;

namespace CampfireNet.Utilities.ChannelsExtensions {
   public static class ChannelsExtensions {
      public static void Write<T>(this WritableChannel<T> channel, T message) {
         channel.WriteAsync(message).Wait();
      }

      public static Task WriteAsync<T>(this WritableChannel<T> channel, T message) {
         return channel.WriteAsync(message, CancellationToken.None);
      }

      public static T Read<T>(this ReadableChannel<T> channel) {
         return channel.ReadAsync().Result;
      }

      public static Task<T> ReadAsync<T>(this ReadableChannel<T> channel) {
         return channel.ReadAsync(CancellationToken.None, acceptanceTest => true);
      }

      public static Task Run(Func<Task> task) {
         return Task.Run(task);
         //         TaskCompletionSource<byte> tcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
         //         Task.Run(async () => {
         //            await task().ConfigureAwait(false);
         //            tcs.SetResult(0);
         //         });
         //         return tcs.Task;
      }

      public static Task<T> Run<T>(Func<Task<T>> task) {
         return Task.Run(task);
         //         TaskCompletionSource<T> tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
         //         Task.Run(async () => {
         //            tcs.SetResult(await task().ConfigureAwait(false));
         //         });
         //         return tcs.Task;
      }

      public static Task Go(Func<Task> task) => Run(task);

      public static Task<T> Go<T>(Func<Task<T>> task) => Run(task);

      public static ICaseTemporary Case<T>(ReadableChannel<T> channel, Action callback) {
         return new CaseTemporary<T>(channel, Convert<T>(callback));
      }

      public static ICaseTemporary Case<T>(ReadableChannel<T> channel, Action<T> callback) {
         return new CaseTemporary<T>(channel, Convert<T>(callback));
      }

      public static ICaseTemporary Case<T>(ReadableChannel<T> channel, Func<Task> callback) {
         return new CaseTemporary<T>(channel, Convert<T>(callback));
      }

      public static ICaseTemporary Case<T>(ReadableChannel<T> channel, Func<T, Task> callback) {
         return new CaseTemporary<T>(channel, callback);
      }
   }

   public class CaseTemporary<T> : ICaseTemporary {
      private readonly ReadableChannel<T> channel;
      private readonly Func<T, Task> callback;

      public CaseTemporary(ReadableChannel<T> channel, Func<T, Task> callback) {
         this.channel = channel;
         this.callback = callback;
      }

      public void Register(DispatchContext dispatchContext) {
         dispatchContext.Case(channel, callback);
      }
   }

   public interface ICaseTemporary {
      void Register(DispatchContext dispatchContext);
   }

   public class DispatchContext {
      public const int kTimesInfinite = int.MinValue;

      private readonly CancellationTokenSource cts = new CancellationTokenSource();
      private readonly AsyncLatch completionLatch = new AsyncLatch();
      private readonly ConcurrentQueue<Task> tasksToShutdown = new ConcurrentQueue<Task>();
      private int dispatchesRemaining;
      private bool isCompleted = false;

      public DispatchContext(int times) {
         dispatchesRemaining = times;
      }

      public bool IsCompleted => isCompleted;

      public DispatchContext Case<T>(ReadableChannel<T> channel, Action callback) {
         return Case(channel, Convert<T>(callback));
      }

      public DispatchContext Case<T>(ReadableChannel<T> channel, Action<T> callback) {
         return Case(channel, Convert<T>(callback));
      }

      public DispatchContext Case<T>(ReadableChannel<T> channel, Func<Task> callback) {
         return Case(channel, Convert<T>(callback));
      }

      public DispatchContext Case<T>(ReadableChannel<T> channel, Func<T, Task> callback) {
         var task = ProcessCaseAsync<T>(channel, callback);
         tasksToShutdown.Enqueue(task);
         return this;
      }

      private async Task ProcessCaseAsync<T>(ReadableChannel<T> channel, Func<T, Task> callback) {
         try {
            while (!cts.IsCancellationRequested) {
               bool isFinalDispatch = false;
               var result = await channel.ReadAsync(
                  cts.Token,
                  acceptanceTest => {
                     if (Interlocked.CompareExchange(ref dispatchesRemaining, 0, 0) == kTimesInfinite) {
                        return true;
                     } else {
                        var spinner = new SpinWait();
                        while (true) {
                           var capturedDispatchesRemaining = Interlocked.CompareExchange(ref dispatchesRemaining, 0, 0);
                           var nextDispatchesRemaining = capturedDispatchesRemaining - 1;

                           if (nextDispatchesRemaining < 0) {
                              return false;
                           }

                           if (Interlocked.CompareExchange(ref dispatchesRemaining, nextDispatchesRemaining, capturedDispatchesRemaining) == capturedDispatchesRemaining) {
                              isFinalDispatch = nextDispatchesRemaining == 0;
                              return true;
                           }
                           spinner.SpinOnce();
                        }
                     }
                  }).ConfigureAwait(false);
               //               Console.WriteLine("Got from ch " + result);
               if (isFinalDispatch) {
                  cts.Cancel();
                  await callback(result).ConfigureAwait(false);
                  completionLatch.Set();
                  isCompleted = true;
               } else {
                  await callback(result).ConfigureAwait(false);
               }
            }
         } catch (OperationCanceledException) {
            // do nothing
         } catch (Exception e) {
            Console.Error.WriteLine(e);
            throw;
         }
      }

      public async Task WaitAsync(CancellationToken token = default(CancellationToken)) {
         await completionLatch.WaitAsync(token).ConfigureAwait(false);
      }

      public async Task ShutdownAsync() {
         cts.Cancel();
         completionLatch.Set();
         foreach (var task in tasksToShutdown) {
            try {
               await task.ConfigureAwait(false);
            } catch (TaskCanceledException) {
               // okay
            }
         }
      }
   }

   public static class ToFuncTTaskConverter {
      public static Func<T, Task> Convert<T>(Action callback) {
         return Convert<T>(t => callback());
      }

      public static Func<T, Task> Convert<T>(Action<T> callback) {
         return t => {
            callback(t);
            return Task.CompletedTask;
         };
      }

      public static Func<T, Task> Convert<T>(Func<Task> callback) {
         return t => callback();
      }
   }

   public class BlockingChannel<T> : Channel<T> {
      private readonly ConcurrentQueue<WriterContext<T>> writerQueue = new ConcurrentQueue<WriterContext<T>>();
      private readonly AsyncSemaphore queueSemaphore = new AsyncSemaphore(0);

      public int Count => queueSemaphore.Count;

      public async Task WriteAsync(T message, CancellationToken cancellationToken) {
         var context = new WriterContext<T>(message);
         writerQueue.Enqueue(context);
         queueSemaphore.Release();
         try {
            await context.completionLatch.WaitAsync(cancellationToken).ConfigureAwait(false);
         } catch (OperationCanceledException) {
            while (true) {
               var originalValue = Interlocked.CompareExchange(ref context.state, WriterContext<T>.kStateCancelled, WriterContext<T>.kStatePending);
               if (originalValue == WriterContext<T>.kStatePending) {
                  throw;
               } else if (originalValue == WriterContext<T>.kStateCompleting) {
                  await context.completingFreedEvent.WaitAsync(CancellationToken.None).ConfigureAwait(false);
               } else if (originalValue == WriterContext<T>.kStateCompleted) {
                  return;
               }
            }
         } finally {
            Trace.Assert(context.state == WriterContext<T>.kStateCancelled ||
                         context.state == WriterContext<T>.kStateCompleted);
         }
      }

      public bool TryRead(out T message) {
         if (!queueSemaphore.TryTake()) {
            message = default(T);
            return false;
         }
         SpinWait spinner = new SpinWait();
         WriterContext<T> context;
         while (!writerQueue.TryDequeue(out context)) {
            spinner.SpinOnce();
         }
         var oldState = Interlocked.CompareExchange(ref context.state, WriterContext<T>.kStateCompleting, WriterContext<T>.kStatePending);
         if (oldState == WriterContext<T>.kStatePending) {
            Interlocked.CompareExchange(ref context.state, WriterContext<T>.kStateCompleted, WriterContext<T>.kStateCompleting);
            context.completingFreedEvent.Set();
            context.completionLatch.Set();
            message = context.value;
            return true;
         } else if (oldState == WriterContext<T>.kStateCompleted) {
            throw new InvalidStateException();
         } else if (oldState == WriterContext<T>.kStateCompleted) {
            throw new InvalidStateException();
         } else if (oldState == WriterContext<T>.kStateCompleted) {
            message = default(T);
            return false;
         } else {
            throw new InvalidStateException();
         }
      }

      public async Task<T> ReadAsync(CancellationToken cancellationToken, Func<T, bool> acceptanceTest) {
         while (!cancellationToken.IsCancellationRequested) {
            await queueSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            WriterContext<T> context;
            if (!writerQueue.TryDequeue(out context)) {
               throw new InvalidStateException();
            }
            var oldState = Interlocked.CompareExchange(ref context.state, WriterContext<T>.kStateCompleting, WriterContext<T>.kStatePending);
            if (oldState == WriterContext<T>.kStatePending) {
               if (acceptanceTest(context.value)) {
                  Interlocked.CompareExchange(ref context.state, WriterContext<T>.kStateCompleted, WriterContext<T>.kStateCompleting);
                  context.completingFreedEvent.Set();
                  context.completionLatch.Set();
                  return context.value;
               } else {
                  Interlocked.CompareExchange(ref context.state, WriterContext<T>.kStatePending, WriterContext<T>.kStateCompleting);
                  context.completingFreedEvent.Set();
                  writerQueue.Enqueue(context);
                  queueSemaphore.Release();
               }
            } else if (oldState == WriterContext<T>.kStateCompleting) {
               throw new InvalidStateException();
            } else if (oldState == WriterContext<T>.kStateCompleted) {
               throw new InvalidStateException();
            } else if (oldState == WriterContext<T>.kStateCancelled) {
               continue;
            }
         }
         // throw is guaranteed
         cancellationToken.ThrowIfCancellationRequested();
         throw new InvalidStateException();
      }

      private class WriterContext<T> {
         public const int kStatePending = 0;
         public const int kStateCompleting = 1;
         public const int kStateCompleted = 2;
         public const int kStateCancelled = 3;

         public readonly AsyncLatch completionLatch = new AsyncLatch();
         public readonly AsyncAutoResetLatch completingFreedEvent = new AsyncAutoResetLatch();
         public readonly T value;
         public int state = kStatePending;

         public WriterContext(T value) {
            this.value = value;
         }
      }
   }


}