using k8s;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Threading;

public class Worker : BackgroundService
{
  private readonly Kubernetes _k8s;
  private readonly ILogger<Worker> _log;
  private readonly IConfiguration _config;

  private readonly string podName;
  private static readonly string podNamespace = "default";

  public Worker(ILogger<Worker> logger, IConfiguration config, Kubernetes client)
  {
    _log = logger;
    _config = config;
    _k8s = client;
    podName="add-reg-key-"+_config["NODE_NAME"];
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    //Create a host process container pod on this node that adds a registry key
    var keyAdded = await AddRegKey();
    //Wait for the pod to complete
    var success = WaitForPodCompletion(stoppingToken);
    if (success && !stoppingToken.IsCancellationRequested)
    {
      //Remove the node taint
      await RemoveTaint();
      //Label node as patched
      await LabelNode();
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
        Name = podName,
        NamespaceProperty = podNamespace
      },
      Spec = new V1PodSpec
      {
        NodeName = nodeName,
        Containers = new List<V1Container>
        {
          new V1Container
          {
            Name = "add-reg-key",
            Image = "mcr.microsoft.com/windows/nanoserver:ltsc2019",
            Command = new List<string> { "cmd.exe" },
            Args = new List<string> { 
              "/c", 
              "reg", 
              "add", 
              @"HKLM\SYSTEM\CurrentControlSet\Services\hns\State", 
              "/v",
              "DNSMaximumTTL",
              "/t",
              "REG_DWORD", 
              "/d",
              "30", 
              "/f" 
            }
          }
        },
        RestartPolicy = "Never",
        HostNetwork = true,
        SecurityContext = new V1PodSecurityContext
        {
          WindowsOptions = new V1WindowsSecurityContextOptions
          {
            HostProcess = true,
            RunAsUserName = "NT AUTHORITY\\SYSTEM"
          }
        },
        Tolerations = new List<V1Toleration>
        {
          new V1Toleration
          {
            Key = taint.Key,
            Effect = taint.Effect,
            Value = taint.Value
          }
        }
      }
    };

    try {
      var result = await _k8s.CreateNamespacedPodAsync(pod, podNamespace);
      return result != null;
    }
    catch (Exception ex)
    {
      _log.LogError(ex, "Error creating pod");
      return false;
    }
  }

  private bool WaitForPodCompletion(CancellationToken stoppingToken)
  {
    try {
      var pod = _k8s.ReadNamespacedPodStatus(podName, podNamespace);
      while(pod.Status.Phase != "Succeeded" && !stoppingToken.IsCancellationRequested)
      {
        Task.Delay(5000, stoppingToken);
        pod = _k8s.ReadNamespacedPodStatus(podName, podNamespace);
      }
      return pod.Status.Phase == "Succeeded";
    }
    catch (k8s.Autorest.HttpOperationException ex) {
      _log.LogError(ex, "Error watching pod");
      _log.LogError(ex.Response.Content);
      return false;
    }
  }

  private async Task<bool> RemoveTaint()
  {
    var nodeName = _config["NODE_NAME"];
    var taint = new V1Taint().Parse(_config["TAINT"]);
    try {
      var node = _k8s.ReadNode(nodeName);
      var newTaints = new List<V1Taint>();
      if (node.Spec.Taints != null && node.Spec.Taints.Any())
      {
        foreach (var t in node.Spec.Taints)
        {
          if (!t.Equals(taint))
          {
            newTaints.Add(t);
          }
        }
      }
      node.Spec.Taints = newTaints;
      var result = await _k8s.ReplaceNodeAsync(node, nodeName);
      return result.Spec.Taints == null || !result.Spec.Taints.Any(t=>t.Equals(taint));
    }
    catch (k8s.Autorest.HttpOperationException ex)
    {
      _log.LogError(ex, "Error removing taint");
      _log.LogError(ex.Response.Content);
      return false;
    }
  }

  private async Task<bool> LabelNode()
  {
    var nodeName = _config["NODE_NAME"];
    try {
      var node = _k8s.ReadNode(nodeName);
      if (node.Metadata.Labels.Any(l=>l.Key == "RegistryPatched"))
        return true;
      node.Metadata.Labels.Add("RegistryPatched", "true");
      var result = await _k8s.ReplaceNodeAsync(node, nodeName);
      return result.Metadata.Labels.ContainsKey("RegistryPatched");
    }
    catch (k8s.Autorest.HttpOperationException  ex)
    {
      _log.LogError(ex, "Error labeling node");
      _log.LogError(ex.Response.Content);
      return false;
    }
  }
}
