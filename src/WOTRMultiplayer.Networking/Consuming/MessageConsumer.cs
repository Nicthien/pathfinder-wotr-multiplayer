using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Logging.Extensions;
using WOTRMultiplayer.Networking.Abstractions;

namespace WOTRMultiplayer.Networking.Consuming
{
    public class MessageConsumer : IMessageConsumer
    {
        private readonly ConcurrentDictionary<Type, SortedList<int, Action<long, object>>> _consumers = [];
        private BlockingCollection<NetworkMessageMetadata> _messages = [];
        private readonly object _actionLock = new();
        private readonly ILogger<MessageConsumer> _logger;

        private CancellationTokenSource _cancellation;
        private bool _isRunning = false;
        private long _generation;

        public MessageConsumer(ILogger<MessageConsumer> logger)
        {
            _logger = logger;
        }

        public void Reset()
        {
            lock (_actionLock)
            {
                _cancellation?.Cancel();
                _isRunning = false;
                _messages = [];
            }
        }

        public void On<TMessage>(Action<long, TMessage> messageHandler, MessageHandlerPriority messageHandlerPriority)
            where TMessage : class
        {
            var handlers = _consumers.GetOrAdd(typeof(TMessage), []);
            var priority = messageHandlerPriority switch
            {
                MessageHandlerPriority.High => handlers.Keys.Count == 0 ? -1 : handlers.Keys.Min() - 1,
                _ or MessageHandlerPriority.Default => handlers.Keys.Count == 0 ? 0 : handlers.Keys.Max() + 1
            };
            handlers.Add(priority, (player, message) => messageHandler(player, (TMessage)message));
        }

        public void Enqueue(NetworkMessageMetadata message)
        {
            _messages.Add(message);

            if (!_isRunning)
            {
                lock (_actionLock)
                {
                    if (!_isRunning)
                    {
                        _isRunning = true;
                        _cancellation = new CancellationTokenSource();
                        var cancellation = _cancellation;
                        var messages = _messages;
                        var generation = Interlocked.Increment(ref _generation);
                        Task.Run(() => Consume(generation, cancellation, messages), cancellation.Token);
                    }
                }
            }
        }

        private void Consume(long generation, CancellationTokenSource cancellation,
            BlockingCollection<NetworkMessageMetadata> messages)
        {
            _logger.LogInformation("Message consumer has been started");

            try
            {
                foreach (var metadata in messages.GetConsumingEnumerable(cancellation.Token))
                {
                    if (cancellation.Token.IsCancellationRequested)
                    {
                        continue;
                    }

                    var messageType = metadata.Message.GetType();
                    if (!_consumers.TryGetValue(messageType, out var configuredHandlers))
                    {
                        _logger.LogError("There are no configured message handlers. Type={Type}", messageType);
                        continue;
                    }

                    _logger.LogObject(LogLevel.Information, "Processing {MessageType}. Channel={Channel}, PlayerId={PlayerId}, ReceivedFrom={ReceivedFrom}, Consumers={Consumers}", metadata.Message, metadata.ChannelType, metadata.PlayerId, metadata.ClientId, configuredHandlers.Count);

                    var handlers = configuredHandlers.ToList();
                    foreach (var handler in handlers)
                    {
                        try
                        {
                            handler.Value.Invoke(metadata.PlayerId, metadata.Message);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error while consuming message. Channel={Channel}, PlayerId={PlayerId}, ReceivedFrom={ReceivedFrom}, Type={Type}", metadata.ChannelType, metadata.PlayerId, metadata.ClientId, messageType);
                        }
                    }
                }
            }
            catch (ThreadAbortException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Logged instead of rethrown: nothing observes this task, and a dead
                // consumer must not leave the pump stopped until the next Reset.
                _logger.LogError(ex, "Unhandled consumer error");
            }

            _logger.LogWarning("Message consumer has been terminated");

            lock (_actionLock)
            {
                if (generation == _generation)
                {
                    _isRunning = false;
                }
            }
        }
    }
}
