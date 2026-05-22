using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Services.PluginIngestion;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace Backend.API.Tests;

public sealed class PluginIngestionAdminControllerTests
{
    [Test]
    public async Task Get_returns_filtered_paged_status_records()
    {
        var store = new InMemoryPluginIngestionStatusStore();
        await store.AppendEventAsync(
            PluginIngestionStatusUpdate.FromPlugin(
                "PluginA_1.0.0.zip",
                "PluginA",
                new Version(1, 0, 0),
                PluginIngestionStatus.Registered),
            CancellationToken.None);
        await store.AppendEventAsync(
            PluginIngestionStatusUpdate.FromPlugin(
                "PluginB_1.0.0.zip",
                "PluginB",
                new Version(1, 0, 0),
                PluginIngestionStatus.AbiInvalid,
                "bad abi"),
            CancellationToken.None);

        var controller = new PluginIngestionAdminController(store);

        var result = await controller.Get(
            pluginName: null,
            status: PluginIngestionStatus.AbiInvalid,
            quarantined: null,
            continuationToken: null,
            pageSize: 10,
            cancellationToken: CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var page = ok.Value.ShouldBeOfType<PluginIngestionStatusPage>();
        page.Items.Count.ShouldBe(1);
        page.Items[0].PluginName.ShouldBe("PluginB");
    }

    [Test]
    public async Task GetById_returns_detail_record()
    {
        var store = new InMemoryPluginIngestionStatusStore();
        var id = PluginIngestionStatusIds.FromBlobName("PluginA_1.0.0.zip");
        await store.AppendEventAsync(
            PluginIngestionStatusUpdate.FromPlugin(
                "PluginA_1.0.0.zip",
                "PluginA",
                new Version(1, 0, 0),
                PluginIngestionStatus.Registered),
            CancellationToken.None);
        var controller = new PluginIngestionAdminController(store);

        var result = await controller.GetById(id, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var record = ok.Value.ShouldBeOfType<PluginIngestionStatusRecord>();
        record.Id.ShouldBe(id);
    }

    [Test]
    public async Task Quarantine_and_unquarantine_toggle_retry_gating()
    {
        var store = new InMemoryPluginIngestionStatusStore();
        var blobName = "PluginA_1.0.0.zip";
        var id = PluginIngestionStatusIds.FromBlobName(blobName);
        await store.AppendEventAsync(
            PluginIngestionStatusUpdate.FromPlugin(
                blobName,
                "PluginA",
                new Version(1, 0, 0),
                PluginIngestionStatus.Detected),
            CancellationToken.None);
        var controller = new PluginIngestionAdminController(store);

        await controller.Quarantine(
            id,
            new PluginIngestionQuarantineRequest("operator hold"),
            CancellationToken.None);

        (await store.IsQuarantinedAsync(blobName, CancellationToken.None)).ShouldBeTrue();

        await controller.Unquarantine(
            id,
            new PluginIngestionQuarantineRequest("retry"),
            CancellationToken.None);

        (await store.IsQuarantinedAsync(blobName, CancellationToken.None)).ShouldBeFalse();
    }
}
