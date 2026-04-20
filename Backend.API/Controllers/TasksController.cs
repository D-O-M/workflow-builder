using Backend.Interfaces.Repositories;
using Backend.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[ApiController]
[Route("[controller]")]
public class TasksController : ControllerBase
{
    private readonly ITaskDefinitionRepository _taskDefinitionRepository;

    public TasksController(ITaskDefinitionRepository taskDefinitionRepository)
    {
        _taskDefinitionRepository = taskDefinitionRepository;
    }

    private static readonly TaskCatalogEntry[] Tasks = new[]
    {
        new TaskCatalogEntry { Name = "Data Ingestion", Channels = 2, Description = "Import data from external APIs." },
        new TaskCatalogEntry { Name = "Normalize Records", Channels = 1, Description = "Clean up and normalize incoming data." },
        new TaskCatalogEntry { Name = "Generate Summaries", Channels = 3, Description = "Produce short summaries for reports." },
        new TaskCatalogEntry { Name = "Train Model", Channels = 4, Description = "Run machine learning training pipeline." },
        new TaskCatalogEntry { Name = "Validate Metrics", Channels = 2, Description = "Check accuracy and other metrics." },
        new TaskCatalogEntry { Name = "Publish Dashboard", Channels = 1, Description = "Send results to the dashboard." },
        new TaskCatalogEntry { Name = "Archive Logs", Channels = 2, Description = "Move logs to long-term storage." },
        new TaskCatalogEntry { Name = "Send Notifications", Channels = 1, Description = "Notify stakeholders via email." },
        new TaskCatalogEntry { Name = "Scale Resources", Channels = 5, Description = "Adjust infrastructure as needed." },
        new TaskCatalogEntry { Name = "Clean Temp Files", Channels = 1, Description = "Remove temporary working files." },
    };

    [HttpGet]
    public async Task<ActionResult<List<TaskDefinitionViewModel>>> Get(CancellationToken cancellationToken) =>
        await _taskDefinitionRepository.GetAllTaskDefinitionViewModels(cancellationToken);
}
