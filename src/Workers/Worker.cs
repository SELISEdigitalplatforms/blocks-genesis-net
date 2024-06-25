using Amazon.Runtime.Internal;
using Azure.Messaging.ServiceBus;
using System.Threading;

namespace WorkerOne
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly ServiceBusClient _serviceBusClient;
        private readonly List<string> _queueNames = new List<string> { "DemoQueue", "DemoQueue1" };
        private readonly List<ServiceBusProcessor> _processors = new List<ServiceBusProcessor>();

        //private readonly Dictionary<string, string> _queueNames = new Dictionary<string, string>
        //{
        //    {"Queue1", "<your_queue_name_1>"},
        //    {"Queue2", "<your_queue_name_2>"}
        //};

        private readonly Dictionary<string, (string topicName, string subscriptionName)> _topicSubscriptions = new Dictionary<string, (string, string)>
        {
            {"Subscription1", ("<your_topic_name_1>", "<your_subscription_name_1>")},
            {"Subscription2", ("<your_topic_name_2>", "<your_subscription_name_2>")}
        };

        public Worker(ILogger<Worker> logger, ServiceBusClient serviceBusClient)
        {
            _logger = logger;
            _serviceBusClient = serviceBusClient;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var queueName in _queueNames)
            {
                var processor = _serviceBusClient.CreateProcessor(queueName, new ServiceBusProcessorOptions());
                processor.ProcessMessageAsync += MessageHandler;
                processor.ProcessErrorAsync += ErrorHandler;
                _processors.Add(processor);
                await processor.StartProcessingAsync(cancellationToken);
            }

            //foreach (var (topicName, subscriptionName) in _topicSubscriptions.Values)
            //{
            //    var processor = _serviceBusClient.CreateProcessor(topicName, subscriptionName, new ServiceBusProcessorOptions());
            //    processor.ProcessMessageAsync += async args =>
            //    {
            //        string body = args.Message.Body.ToString();
            //        _logger.LogInformation($"Received message from topic {topicName}, subscription {subscriptionName}: {body}");
            //        await args.CompleteMessageAsync(args.Message);
            //    };
            //    processor.ProcessErrorAsync += ErrorHandler;
            //    _processors.Add(processor);
            //    await processor.StartProcessingAsync(cancellationToken);
            //}

            _logger.LogInformation("Worker started at: {time}", DateTimeOffset.Now);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var processor in _processors)
            {
                await processor.StopProcessingAsync(cancellationToken);
            }

            await base.StopAsync(cancellationToken);
            _logger.LogInformation("Worker stopped at: {time}", DateTimeOffset.Now);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
           
        }
        static async Task MessageHandler(ProcessMessageEventArgs args)
        {
            string body = args.Message.Body.ToString();
            Console.WriteLine($"Message received: {body}");

            await args.CompleteMessageAsync(args.Message);
        }

        static Task ErrorHandler(ProcessErrorEventArgs args)
        {
            Console.WriteLine(args.Exception.ToString());
            return Task.CompletedTask;
        }

    }
}
