using Aplication.DTOs;
using Aplication.UseCases;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace Infrastructure.Messaging
{
    public class RabbitMqConsumerHostedService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _config;
        private readonly ILogger<RabbitMqConsumerHostedService> _logger;
        private IConnection? _connection;
        private IModel? _channel;

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
                    var factory = new ConnectionFactory { HostName = hostName, DispatchConsumersAsync = true };
                    _connection = factory.CreateConnection();
                    _channel = _connection.CreateModel();

                    // Exchange tipo Fanout o Topic para recibir los eventos de todos los grupos
                    _channel.ExchangeDeclare(exchange: exchangeName, type: ExchangeType.Topic, durable: true);
                    _channel.QueueDeclare(queue: queueName, durable: true, exclusive: false, autoDelete: false);
                    _channel.QueueBind(queue: queueName, exchange: exchangeName, routingKey: "#");

                    var consumer = new AsyncEventingBasicConsumer(_channel);
                    consumer.Received += async (model, ea) =>
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
                            _channel.BasicAck(ea.DeliveryTag, false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error procesando mensaje de RabbitMQ: {ex.Message}");
                            _channel.BasicNack(ea.DeliveryTag, false, false);
                        }
                    };

                    _channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
                    _logger.LogInformation("Conectado a RabbitMQ. Escuchando eventos de todos los grupos...");

                    await Task.Delay(Timeout.Infinite, stoppingToken);
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
            _channel?.Close();
            _connection?.Close();
            base.Dispose();
        }
    }
}