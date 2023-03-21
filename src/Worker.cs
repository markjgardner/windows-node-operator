using k8s;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

public class Worker : BackgroundService
{
  private readonly Kubernetes _k8s;
  private readonly ILogger<Worker> _log;
  private readonly IConfiguration _config;

  private static readonly string podName = "add-reg-key";
  private static readonly string podNamespace = "default";

  public Worker(ILogger<Worker> logger, IConfiguration config, Kubernetes client)
  {
    _log = logger;
    _config = config;
    _k8s = client;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while(stoppingToken.IsCancellationRequested == false)
    {
      //Create a host process container pod on this node that adds a registry key
      var keyAdded = await AddRegKey();
      //Wait for the pod to complete
      var success = WaitForPodCompletion(stoppingToken);
      if (success && !stoppingToken.IsCancellationRequested)
      {
        //Remove the node taint
        var untainted = await RemoveTaint();
      }
    }
  }
  private async Task<bool> AddRegKey()
  {
    var nodeName = _config["NODE_NAME"];    
    var taint = new V1Taint().Parse(_config["TAINT"]);
    
    //Create a host process container pod on this node that adds a registry key
    var pod = new V1Pod
    {
      Metadata = new V1ObjectMeta
      {
        Name = "podName",
        NamespaceProperty = "podNamespace"
      },
      Spec = new V1PodSpec
      {
        NodeName = nodeName,
        Containers = new List<V1Container>
        {
          new V1Container
          {
            Name = "add-reg-key",
            Image = "mcr.microsoft.com/oss/kubernetes/windows-host-process-containers-base-image:v1.0.0",
            Command = new List<string> { "cmd.exe" },
            Args = new List<string> { "/c", "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\hns\\State\" /v DNSMaximumTTL /t REG_DWORD /d \"30\" /f" },
            SecurityContext = new V1SecurityContext
            {
              Privileged = true
            }
          }
        }
      }
    };

    var result = await _k8s.CreateNamespacedPodAsync(pod, podNamespace);

    return result != null;

  }

  private bool WaitForPodCompletion(CancellationToken stoppingToken)
  {
    var pod = _k8s.ReadNamespacedPodStatus(podName, podNamespace);
    while(pod.Status.Phase != "Succeeded" && !stoppingToken.IsCancellationRequested)
    {
      Task.Delay(5000, stoppingToken);
      pod = _k8s.ReadNamespacedPodStatus(podName, podNamespace);
    }
    return pod.Status.Phase != "Succeeded";
  }

  private async Task<bool> RemoveTaint()
  {
    var nodeName = _config["NODE_NAME"];
    var taint = new V1Taint().Parse(_config["TAINT"]);
    var node = _k8s.ReadNode(nodeName);
    node.Spec.Taints.Remove(taint);
    var result = await _k8s.ReplaceNodeAsync(node, nodeName);
    return result.Spec.Taints.Contains(taint);
  }
}
