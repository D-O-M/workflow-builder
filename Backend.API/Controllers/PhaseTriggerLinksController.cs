using System.Text.Encodings.Web;
using System.Text.Json;
using Backend.API.Services;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/phase-trigger-links")]
public sealed class PhaseTriggerLinksController : ControllerBase
{
    private static readonly HashSet<string> ReservedQueryParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "seriesKey",
        "targetPhaseIndex",
        "phasedBlueprintVersionId"
    };

    private readonly IPhasedBlueprintRepository _phasedBlueprintRepository;
    private readonly PhasedBlueprintProcessService _phasedBlueprintProcessService;

    public PhaseTriggerLinksController(
        IPhasedBlueprintRepository phasedBlueprintRepository,
        PhasedBlueprintProcessService phasedBlueprintProcessService)
    {
        _phasedBlueprintRepository = phasedBlueprintRepository;
        _phasedBlueprintProcessService = phasedBlueprintProcessService;
    }

    [HttpGet("activate")]
    public async Task<IActionResult> Activate(
        [FromQuery] string seriesKey,
        [FromQuery] int targetPhaseIndex,
        [FromQuery] int? phasedBlueprintVersionId)
    {
        if (string.IsNullOrWhiteSpace(seriesKey) || targetPhaseIndex <= 0)
        {
            return Html(
                StatusCodes.Status400BadRequest,
                "Phase trigger link is incomplete.",
                "The link must include a phase series key and a target phase index.");
        }

        try
        {
            var series = await _phasedBlueprintRepository.GetByKeyAsync(
                seriesKey,
                HttpContext.RequestAborted);
            var accepted = await _phasedBlueprintProcessService.TriggerPhaseAsync(
                series.Id,
                targetPhaseIndex,
                new PhasedBlueprintTriggerInputModel
                {
                    PhasedBlueprintVersionId = phasedBlueprintVersionId,
                    TriggerInput = BuildTriggerInput(Request.Query)
                },
                HttpContext.RequestAborted);

            return Html(
                StatusCodes.Status202Accepted,
                "Deployment phase started.",
                $"Phase {accepted.PhaseIndex + 1} is running as child process {accepted.ChildProcessId}.");
        }
        catch (EntityNotFoundException<PhasedBlueprintViewModel>)
        {
            return Html(
                StatusCodes.Status404NotFound,
                "Phase series was not found.",
                "WorkflowBuilder could not find a phase series for this link.");
        }
        catch (EntityNotFoundException<PhaseCorrelationRecord>)
        {
            return Html(
                StatusCodes.Status404NotFound,
                "Deployment phase is not ready yet.",
                "The build phase has not registered matching correlation values for this trigger.");
        }
        catch (EntityNotFoundException)
        {
            return Html(
                StatusCodes.Status404NotFound,
                "Deployment phase is not ready yet.",
                "WorkflowBuilder could not resolve a pending phase for this link.");
        }
        catch (InvalidDataException exception)
        {
            return Html(
                StatusCodes.Status400BadRequest,
                "Phase trigger could not be accepted.",
                exception.Message);
        }
    }

    private static Dictionary<string, JsonElement> BuildTriggerInput(
        IEnumerable<KeyValuePair<string, StringValues>> query)
    {
        var triggerInput = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, values) in query)
        {
            if (ReservedQueryParameters.Contains(key))
            {
                continue;
            }

            var value = values.Count == 0 ? string.Empty : values[0] ?? string.Empty;
            triggerInput[key] = JsonSerializer.SerializeToElement(value);
        }

        return triggerInput;
    }

    private static ContentResult Html(int statusCode, string title, string detail)
    {
        var encodedTitle = HtmlEncoder.Default.Encode(title);
        var encodedDetail = HtmlEncoder.Default.Encode(detail);
        return new ContentResult
        {
            StatusCode = statusCode,
            ContentType = "text/html; charset=utf-8",
            Content = $$"""
                <!doctype html>
                <html lang="en">
                <head>
                  <meta charset="utf-8" />
                  <meta name="viewport" content="width=device-width, initial-scale=1" />
                  <title>{{encodedTitle}}</title>
                  <style>
                    body { font-family: system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f7f8fb; color: #1f2937; }
                    main { width: min(34rem, calc(100vw - 2rem)); padding: 2rem; border: 1px solid #d8dee9; border-radius: 8px; background: white; box-shadow: 0 12px 36px rgb(15 23 42 / 10%); }
                    h1 { margin: 0 0 .75rem; font-size: 1.35rem; }
                    p { margin: 0; color: #4b5563; line-height: 1.5; }
                  </style>
                </head>
                <body>
                  <main>
                    <h1>{{encodedTitle}}</h1>
                    <p>{{encodedDetail}}</p>
                  </main>
                </body>
                </html>
                """
        };
    }
}
