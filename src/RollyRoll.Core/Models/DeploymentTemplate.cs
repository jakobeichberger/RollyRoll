namespace RollyRoll.Core.Models;

/// <summary>
/// A recorded rollout configuration — reusable template for deployments.
/// Defines what image to deploy, how to deploy it, and what post-deployment steps to run.
/// </summary>
public class DeploymentTemplate
{
    public int Id { get; set; }

    /// <summary>Template name, e.g. "Standard Win11 Workstation" or "Developer PC Setup".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Description of what this template does.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The image to deploy (null = prompt at deploy time).</summary>
    public int? ImageId { get; set; }
    public Image? Image { get; set; }

    /// <summary>Default deploy mode: clean install or restore with user profiles.</summary>
    public DeployMode DefaultDeployMode { get; set; } = DeployMode.CleanDeploy;

    /// <summary>Whether to automatically send WoL before deployment.</summary>
    public bool SendWakeOnLan { get; set; } = true;

    /// <summary>Whether to join the PC to a domain after deployment.</summary>
    public bool JoinDomain { get; set; }

    /// <summary>Target OU for domain join (LDAP format).</summary>
    public string? DomainOuPath { get; set; }

    /// <summary>
    /// Post-deployment steps to execute in order (JSON-serialized list of DeploymentStep).
    /// Each step can be: RunScript, InstallMsi, InstallExe, CopyFiles, RegistryEdit, Reboot.
    /// </summary>
    public string PostDeployStepsJson { get; set; } = "[]";

    /// <summary>Whether to reboot after all steps complete.</summary>
    public bool RebootAfterDeploy { get; set; } = true;

    /// <summary>Timeout in minutes for the entire deployment before marking as failed.</summary>
    public int TimeoutMinutes { get; set; } = 120;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// A single post-deployment step within a template.
/// Serialized as JSON in DeploymentTemplate.PostDeployStepsJson.
/// </summary>
public class DeploymentStep
{
    public int Order { get; set; }
    public string Name { get; set; } = string.Empty;
    public DeploymentStepType StepType { get; set; }

    /// <summary>Command or path to execute (script path, MSI path, etc.).</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Arguments to pass to the command.</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Whether to continue the deployment if this step fails.</summary>
    public bool ContinueOnFailure { get; set; }

    /// <summary>Timeout for this individual step in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 600;
}

public enum DeploymentStepType
{
    RunScript,
    InstallMsi,
    InstallExe,
    CopyFiles,
    RegistryEdit,
    Reboot,
    WaitForUser
}
