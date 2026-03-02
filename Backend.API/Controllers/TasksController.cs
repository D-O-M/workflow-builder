using Backend.Interfaces.Repositories;
using Backend.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[ApiController]
[Route("[controller]")]
public class TasksController : ControllerBase
{
    private readonly IWorkflowTaskRepository _workflowTaskRepository;

    public TasksController(IWorkflowTaskRepository workflowTaskRepository)
    {
        _workflowTaskRepository = workflowTaskRepository;
    }

    private static readonly WorkflowTask[] Tasks = new[]
    {
        new WorkflowTask { Name = "Data Ingestion", Channels = 2, Description = "Import data from external APIs." },
        new WorkflowTask { Name = "Normalize Records", Channels = 1, Description = "Clean up and normalize incoming data." },
        new WorkflowTask { Name = "Generate Summaries", Channels = 3, Description = "Produce short summaries for reports." },
        new WorkflowTask { Name = "Train Model", Channels = 4, Description = "Run machine learning training pipeline." },
        new WorkflowTask { Name = "Validate Metrics", Channels = 2, Description = "Check accuracy and other metrics." },
        new WorkflowTask { Name = "Publish Dashboard", Channels = 1, Description = "Send results to the dashboard." },
        new WorkflowTask { Name = "Archive Logs", Channels = 2, Description = "Move logs to long-term storage." },
        new WorkflowTask { Name = "Send Notifications", Channels = 1, Description = "Notify stakeholders via email." },
        new WorkflowTask { Name = "Scale Resources", Channels = 5, Description = "Adjust infrastructure as needed." },
        new WorkflowTask { Name = "Clean Temp Files", Channels = 1, Description = "Remove temporary working files." },
    };

    [HttpGet]
    public async Task<ActionResult<List<TaskDefinitionViewModel>>> Get(CancellationToken cancellationToken) =>
        await _workflowTaskRepository.GetAllTaskDefinitionViewModels(cancellationToken);
}
