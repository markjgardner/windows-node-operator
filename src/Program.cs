using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

var host = Host.CreateDefaultBuilder(args)
  .ConfigureServices(services =>
  {
    services.AddHostedService<Worker>();
    services.AddSingleton((s) => {
      var config = KubernetesClientConfiguration.BuildDefaultConfig();
      return new Kubernetes(config);
    });
  })
  .ConfigureLogging((_, logging) => 
  {
    logging.AddSimpleConsole(options => 
    { 
      options.IncludeScopes = true; 
      options.SingleLine = true; 
    });
  })
  .ConfigureAppConfiguration((config) => {
    config.AddEnvironmentVariables();
  })
  .Build();


host.Run();