using Aplication.DTOs;
using Aplication.UseCases;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.Messaging
{
    public class RabbitMqConsumerHostedService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _config;
        private readonly ILogger<RabbitMqConsumerHostedService> _logger;
        private IConnection? _connection;
        private IChannel? _channel;

        public RabbitMqConsumerHostedService(
            IServiceProvider serviceProvider,
            IConfiguration config,
            ILogger<RabbitMqConsumerHostedService> logger)
        {
            _serviceProvider = serviceProvider;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var hostName = _config["RabbitMq:Host"] ?? "localhost";
            var exchangeName = _config["RabbitMq:Exchange"] ?? "aeropuerto_events";
            var queueName = _config["RabbitMq:Queue"] ?? "grupo8_logs_queue";

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var factory = new ConnectionFactory { HostName = hostName };
                    _connection = await factory.CreateConnectionAsync(stoppingToken);
                    _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

                    // Exchange tipo Topic para recibir los eventos de todos los grupos
                    await _channel.ExchangeDeclareAsync(
                        exchange: exchangeName,
                        type: ExchangeType.Topic,
                        durable: true,
                        cancellationToken: stoppingToken);

                    await _channel.QueueDeclareAsync(
                        queue: queueName,
                        durable: true,
                        exclusive: false,
                        autoDelete: false,
                        cancellationToken: stoppingToken);

                    await _channel.QueueBindAsync(
                        queue: queueName,
                        exchange: exchangeName,
                        routingKey: "#",
                        cancellationToken: stoppingToken);

                    var consumer = new AsyncEventingBasicConsumer(_channel);
                    consumer.ReceivedAsync += async (model, ea) =>
                    {
                        var body = ea.Body.ToArray();
                        var json = Encoding.UTF8.GetString(body);

                        try
                        {
                            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                            var envelope = JsonSerializer.Deserialize<BusEventEnvelopeDto>(json, options);

                            if (envelope != null)
                            {
                                using var scope = _serviceProvider.CreateScope();
                                var useCase = scope.ServiceProvider.GetRequiredService<ProcesarEventoUseCase>();
                                await useCase.EjecutarDesdeBusAsync(envelope);
                            }
                            await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error procesando mensaje de RabbitMQ: {ex.Message}");
                            await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
                        }
                    };

                    await _channel.BasicConsumeAsync(
                        queue: queueName,
                        autoAck: false,
                        consumer: consumer,
                        cancellationToken: stoppingToken);

                    _logger.LogInformation("Conectado a RabbitMQ. Escuchando eventos de todos los grupos...");

                    await Task.Delay(Timeout.Infinite, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"RabbitMQ no disponible en '{hostName}' ({ex.Message}). Reintentando en 10s...");
                    await Task.Delay(10000, stoppingToken);
                }
            }
        }

        public override void Dispose()
        {
            _channel?.Dispose();
            _connection?.Dispose();
            base.Dispose();
        }
    }
}