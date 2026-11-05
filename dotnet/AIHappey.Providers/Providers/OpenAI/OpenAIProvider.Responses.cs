using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.StaticFiles;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Output;
using AIHappey.Responses.Streaming;
using ModelContextProtocol.Protocol;
using OpenAI.Containers;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var containerDownloadContext = OpenAiContainerDownloadPolicy.Consume(
            options.ExecutionMetadata,
            DateTimeOffset.UtcNow);

        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());
        options.Store = false;

        var attachmentPreparation = await PrepareContainerAttachmentsAsync(
            options,
            cancellationToken);

        var response = await _client.GetResponses(
                   options,
                   providerId: GetIdentifier(),
                   ct: cancellationToken);

        response = await EnrichResponseResultWithContainerFilesAsync(
            response,
            containerDownloadContext,
            cancellationToken);

        AddUploadedAttachmentToolCall(response, attachmentPreparation);

        AddContainerProviderMetadata(
            response,
            TryGetResponseContainerId(response) ?? attachmentPreparation.ContainerId);

        var effectiveModelId = string.IsNullOrWhiteSpace(response.Model)
            ? options.Model
            : response.Model;
        var effectiveServiceTier = string.IsNullOrWhiteSpace(response.ServiceTier)
            ? options.ServiceTier
            : response.ServiceTier;

        var pricing = OpenAITieredPricingResolver.Resolve(
            effectiveModelId,
            effectiveServiceTier,
            ModelCostMetadataEnricher.GetTotalTokens(response.Usage));

        response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(
            response.Usage,
            response.Metadata,
            pricing);

        response.Metadata = AddOpenAIWebSearchCallCost(
            response.Metadata,
            CountCompletedWebSearchCalls(response.Output));

        return response;
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var containerDownloadContext = OpenAiContainerDownloadPolicy.Consume(
            options.ExecutionMetadata,
            DateTimeOffset.UtcNow);
        var model = await this.GetModel(options.Model, cancellationToken);

        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        options.Store = false;

        var attachmentPreparation = await PrepareContainerAttachmentsAsync(
            options,
            cancellationToken);

        var enrichmentState = new OpenAiResponseStreamEnrichmentState
        {
            ResponseContainerId = attachmentPreparation.ContainerId,
            TurnStartedAtUnixSeconds = containerDownloadContext.TurnStartedAtUnixSeconds
        };
        enrichmentState.SeenCitationKeys.UnionWith(containerDownloadContext.DownloadedFileKeys);

        await foreach (var update in _client.GetResponsesUpdates(options,
            providerId: GetIdentifier(),
            ct: cancellationToken))
        {
            var mappedUpdate = RemapNativeResponseOutputIndex(update, enrichmentState);
            var enrichedUpdates = await EnrichResponseStreamPartWithContainerFilesAsync(
                mappedUpdate,
                enrichmentState,
                cancellationToken);
            if (update is ResponseCreated)
                enrichedUpdates.AddRange(CreateUploadedAttachmentStreamParts(attachmentPreparation, enrichmentState));

            foreach (var enrichedUpdate in enrichedUpdates)
            {
                CaptureCompletedWebSearchCall(enrichedUpdate, enrichmentState);

                if (enrichedUpdate is ResponseCompleted completed)
                {
                    AddContainerProviderMetadata(
                        completed.Response,
                        TryGetResponseContainerId(completed.Response)
                            ?? enrichmentState.ResponseContainerId
                            ?? attachmentPreparation.ContainerId);

                    var effectiveModelId = string.IsNullOrWhiteSpace(completed.Response.Model)
                        ? options.Model
                        : completed.Response.Model;
                    var effectiveServiceTier = string.IsNullOrWhiteSpace(completed.Response.ServiceTier)
                        ? options.ServiceTier
                        : completed.Response.ServiceTier;
                    var pricing = OpenAITieredPricingResolver.Resolve(
                        effectiveModelId,
                        effectiveServiceTier,
                        ModelCostMetadataEnricher.GetTotalTokens(completed.Response.Usage));

                    completed.Response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(
                        completed.Response.Usage,
                        completed.Response.Metadata,
                        pricing);

                    completed.Response.Metadata = AddOpenAIWebSearchCallCost(
                        completed.Response.Metadata,
                        Math.Max(
                            enrichmentState.CompletedWebSearchCallCount,
                            CountCompletedWebSearchCalls(completed.Response.Output)));
                }

                // Provider and gateway events share one monotonically increasing sequence.
                var wire = JsonSerializer.SerializeToNode(enrichedUpdate, ResponseJson.Default)!.AsObject();
                wire["sequence_number"] = enrichmentState.WireSequenceNumber++;
                if (enrichedUpdate is ResponseOutputItemAdded added)
                    enrichmentState.OutputIndexesById[added.Item.Id!] = added.OutputIndex;
                yield return JsonSerializer.Deserialize<ResponseStreamPart>(wire, ResponseJson.Default)!;
            }
        }
    }

    private static ResponseStreamPart RemapNativeResponseOutputIndex(ResponseStreamPart part, OpenAiResponseStreamEnrichmentState state)
    {
        var wire = JsonSerializer.SerializeToNode(part, ResponseJson.Default)!.AsObject();
        if (wire["output_index"] is System.Text.Json.Nodes.JsonValue indexValue && indexValue.TryGetValue<int>(out var nativeIndex))
        {
            if (!state.NativeOutputIndexes.TryGetValue(nativeIndex, out var mappedIndex))
                state.NativeOutputIndexes[nativeIndex] = mappedIndex = state.NextOutputIndex++;
            wire["output_index"] = mappedIndex;
            if (wire["item"]?["id"]?.GetValue<string>() is { } id) state.OutputIndexesById[id] = mappedIndex;
        }
        return JsonSerializer.Deserialize<ResponseStreamPart>(wire, ResponseJson.Default)!;
    }

    private static void CaptureCompletedWebSearchCall(
        ResponseStreamPart update,
        OpenAiResponseStreamEnrichmentState state)
    {
        if (update is not ResponseOutputItemDone done
            || !IsCompletedWebSearchCall(done.Item))
        {
            return;
        }

        var itemId = string.IsNullOrWhiteSpace(done.Item.Id)
            ? $"output_index:{done.OutputIndex}"
            : done.Item.Id;

        state.CompletedWebSearchCallIds.Add(itemId);
    }

    private static int CountCompletedWebSearchCalls(IEnumerable<object>? output)
    {
        if (output is null)
            return 0;

        var count = 0;
        var seenItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var outputItem in output)
        {
            var map = TryGetJsonElementMap(outputItem);
            if (map is null)
                continue;

            var itemType = map.TryGetString("type") ?? string.Empty;
            var itemStatus = map.TryGetString("status") ?? string.Empty;
            if (!string.Equals(itemType, "web_search_call", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(itemStatus, "completed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var itemId = map.TryGetString("id");
            if (string.IsNullOrWhiteSpace(itemId))
            {
                count++;
                continue;
            }

            if (seenItemIds.Add(itemId))
                count++;
        }

        return count;
    }

    private static bool IsCompletedWebSearchCall(ResponseStreamItem? item)
        => item is not null
           && string.Equals(item.Type, "web_search_call", StringComparison.OrdinalIgnoreCase)
           && string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase);



    private static Dictionary<string, object?>? TryConvertGatewayMetadata(object? gatewayObj)
    {
        if (gatewayObj is null)
            return null;

        try
        {
            return JsonSerializer.SerializeToElement(gatewayObj, JsonSerializerOptions.Web)
                .Deserialize<Dictionary<string, object?>>(JsonSerializerOptions.Web);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, JsonElement> ToJsonElementMap(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return [];

        return element.EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static decimal? TryGetDecimal(object? value)
    {
        return value switch
        {
            decimal decimalValue => decimalValue,
            double doubleValue => (decimal)doubleValue,
            float floatValue => (decimal)floatValue,
            int intValue => intValue,
            long longValue => longValue,
            JsonElement { ValueKind: JsonValueKind.Number } jsonNumber when jsonNumber.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonElement { ValueKind: JsonValueKind.String } jsonString when decimal.TryParse(jsonString.GetString(), out var decimalValue) => decimalValue,
            string stringValue when decimal.TryParse(stringValue, out var decimalValue) => decimalValue,
            _ => null
        };
    }

    private async Task<ResponseResult> EnrichResponseResultWithContainerFilesAsync(
        ResponseResult response,
        OpenAiContainerDownloadRequestContext downloadContext,
        CancellationToken cancellationToken)
    {
        var responseOutput = response.Output?.ToList() ?? [];
        var responseContainerId = TryGetResponseContainerId(response);

        var enrichedOutput = new List<object>(responseOutput.Count);
        var seenCitations = new HashSet<string>(
            downloadContext.DownloadedFileKeys,
            StringComparer.OrdinalIgnoreCase);
        var newCitationCount = 0;

        foreach (var outputItem in responseOutput)
        {
            enrichedOutput.Add(outputItem);

            var enrichedItems = await CreateResponseContainerFileArtifactsAsync(
                outputItem,
                seenCitations,
                cancellationToken);

            if (enrichedItems.Count > 0)
            {
                enrichedOutput.AddRange(enrichedItems);
                newCitationCount += enrichedItems.Count;
            }
        }

        if (newCitationCount == 0
            && !string.IsNullOrWhiteSpace(responseContainerId))
        {
            var fallbackDownloads = await TryDownloadAssistantContainerFilesAsync(
                responseContainerId,
                seenCitations,
                downloadContext.TurnStartedAtUnixSeconds,
                cancellationToken);

            foreach (var download in fallbackDownloads)
            {
                AddDownloadArtifacts(enrichedOutput, download);
            }
        }

        response.Output = enrichedOutput;
        return response;
    }

    private async Task<List<ResponseStreamPart>> EnrichResponseStreamPartWithContainerFilesAsync(
        ResponseStreamPart update,
        OpenAiResponseStreamEnrichmentState state,
        CancellationToken cancellationToken)
    {
        var parts = new List<ResponseStreamPart>();
        if (update is not ResponseCompleted) parts.Add(update);

        CaptureResponseContainerId(update, state);

        var generatedImageUpload = await TryUploadGeneratedImageToResponseContainerAsync(
            update,
            state,
            cancellationToken);

        if (generatedImageUpload is not null)
        {
            AddSyntheticCustomToolCallParts(
                parts,
                state,
                generatedImageUpload.ToolCallId,
                UploadGeneratedImageToolName,
                generatedImageUpload.Input,
                CreateGeneratedImageUploadToolCallResult(generatedImageUpload),
                generatedImageUpload.ProviderMetadata);
        }

        if (update is ResponseCompleted
            && !state.DownloadProducedThisTurn
            && !string.IsNullOrWhiteSpace(state.ResponseContainerId)
            && !state.AssistantContainerFallbackAttempted)
        {
            state.AssistantContainerFallbackAttempted = true;

            var fallbackDownloads = await TryDownloadAssistantContainerFilesAsync(
                state.ResponseContainerId,
                state.SeenCitationKeys,
                state.TurnStartedAtUnixSeconds,
                cancellationToken);

            foreach (var fallbackDownload in fallbackDownloads)
            {
                state.DownloadProducedThisTurn = true;
                AddSyntheticCustomToolCallParts(
                    parts,
                    state,
                    fallbackDownload.ToolCallId,
                    DownloadFileToolName,
                    fallbackDownload.Input,
                    CreateDownloadToolCallResult(fallbackDownload),
                    fallbackDownload.ProviderMetadata);
            }
        }

        if (update is not ResponseOutputTextAnnotationAdded annotationAdded
            || !string.Equals(annotationAdded.Annotation.Type, "container_file_citation", StringComparison.OrdinalIgnoreCase))
        {
            if (update is ResponseCompleted completed)
            {
                completed.Response.Output = (completed.Response.Output ?? []).Concat(state.SyntheticOutput)
                    .OrderBy(item =>
                    {
                        var id = JsonSerializer.SerializeToElement(item, ResponseJson.Default).TryGetProperty("id", out var value) ? value.GetString() : null;
                        return id is not null && state.OutputIndexesById.TryGetValue(id, out var index) ? index : int.MaxValue;
                    }).ToList();
                parts.Add(completed);
            }
            return parts;
        }

        var download = await TryDownloadContainerCitationAsync(
            annotationAdded.Annotation,
            state.SeenCitationKeys,
            cancellationToken);

        if (download is null)
            return parts;

        state.DownloadProducedThisTurn = true;
        var syntheticOutputIndex = state.NextOutputIndex++;

        AddSyntheticCustomToolCallParts(
            parts,
            state,
            download.ToolCallId,
            DownloadFileToolName,
            download.Input,
            CreateDownloadToolCallResult(download),
            download.ProviderMetadata,
            syntheticOutputIndex);

        return parts;
    }

    private static void AddSyntheticCustomToolCallParts(
        List<ResponseStreamPart> parts,
        OpenAiResponseStreamEnrichmentState state,
        string toolCallId,
        string toolName,
        JsonElement input,
        CallToolResult output,
        Dictionary<string, Dictionary<string, object>> providerMetadata,
        int? outputIndex = null)
    {
        var syntheticOutputIndex = outputIndex ?? state.NextOutputIndex++;
        var call = SyntheticResponsesTool.Create(toolCallId, toolName, input, output, "openai", providerMetadata);
        state.SyntheticOutput.Add(call);
        state.OutputIndexesById[toolCallId] = syntheticOutputIndex;
        var started = SyntheticResponsesTool.Create(toolCallId, toolName, input, null, "openai", status: "in_progress");
        parts.Add(new ResponseOutputItemAdded
        {
            SequenceNumber = state.NextSequenceNumber++,
            OutputIndex = syntheticOutputIndex,
            Item = started.ToStreamItem()
        });
        parts.Add(new ResponseMcpCallArgumentsDone
        {
            SequenceNumber = state.NextSequenceNumber++,
            OutputIndex = syntheticOutputIndex,
            ItemId = toolCallId,
            Arguments = call.Arguments
        });
        parts.Add(new ResponseOutputItemDone
        {
            SequenceNumber = state.NextSequenceNumber++,
            OutputIndex = syntheticOutputIndex,
            Item = call.ToStreamItem()
        });
    }

    private void AddUploadedAttachmentToolCall(
        ResponseResult response,
        OpenAiContainerAttachmentPreparation preparation)
    {
        if (preparation.UploadedFiles.Count == 0)
            return;

        var toolCallId = Guid.NewGuid().ToString("n");
        var output = response.Output?.ToList() ?? [];
        output.Insert(0, SyntheticResponsesTool.Create(toolCallId, UploadFilesToolName,
            CreateUploadedAttachmentToolInput(preparation), CreateUploadedAttachmentToolResult(preparation.UploadedFiles),
            "openai", CreateUploadedAttachmentProviderMetadata(preparation, toolCallId)));
        response.Output = output;
    }

    private IEnumerable<ResponseStreamPart> CreateUploadedAttachmentStreamParts(
        OpenAiContainerAttachmentPreparation preparation,
        OpenAiResponseStreamEnrichmentState state)
    {
        if (preparation.UploadedFiles.Count == 0)
            yield break;

        var toolCallId = Guid.NewGuid().ToString("n");
        var providerMetadata = CreateUploadedAttachmentProviderMetadata(preparation, toolCallId);
        var parts = new List<ResponseStreamPart>();
        AddSyntheticCustomToolCallParts(parts, state, toolCallId, UploadFilesToolName,
            CreateUploadedAttachmentToolInput(preparation),
            CreateUploadedAttachmentToolResult(preparation.UploadedFiles, preliminary: false, dynamic: true), providerMetadata);
        foreach (var part in parts) yield return part;
    }

    private static JsonElement CreateUploadedAttachmentToolInput(
        OpenAiContainerAttachmentPreparation preparation)
        => JsonSerializer.SerializeToElement(new
        {
            container_id = preparation.ContainerId,
            file_count = preparation.UploadedFiles.Count
        }, JsonSerializerOptions.Web);

    private static CallToolResult CreateUploadedAttachmentToolResult(
        IEnumerable<OpenAiUploadedAttachment> files,
        bool? preliminary = null,
        bool? dynamic = null)
        => new()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                preliminary,
                dynamic,
                files = files.Select(static file => new
                {
                    container_id = file.ContainerId,
                    file_id = file.FileId,
                    filename = file.Filename,
                    path = file.Path,
                    media_type = file.MediaType,
                    source_identity = file.SourceIdentity
                }).ToArray()
            }, JsonSerializerOptions.Web)
        };

    private Dictionary<string, Dictionary<string, object>> CreateUploadedAttachmentProviderMetadata(
        OpenAiContainerAttachmentPreparation preparation,
        string toolCallId)
        => new()
        {
            [GetIdentifier()] = new Dictionary<string, object>
            {
                ["type"] = "container_file_upload_batch",
                ["tool_name"] = UploadFilesToolName,
                ["name"] = UploadFilesToolName,
                ["upload_tool"] = true,
                ["tool_call_id"] = toolCallId,
                ["container_id"] = preparation.ContainerId ?? string.Empty,
                ["file_count"] = preparation.UploadedFiles.Count,
                ["files"] = JsonSerializer.SerializeToElement(
                    preparation.UploadedFiles.Select(static file => new
                    {
                        container_id = file.ContainerId,
                        file_id = file.FileId,
                        filename = file.Filename,
                        path = file.Path,
                        media_type = file.MediaType,
                        source_identity = file.SourceIdentity,
                        raw = file.Raw
                    }),
                    JsonSerializerOptions.Web)
            }
        };

    private static void AddDownloadArtifacts(List<object> result, OpenAiContainerCitationDownload download)
    {
        result.Add(SyntheticResponsesTool.Create(download.ToolCallId, DownloadFileToolName,
            download.Input, CreateDownloadToolCallResult(download), "openai", download.ProviderMetadata));

    }

    private async Task<List<object>> CreateResponseContainerFileArtifactsAsync(
        object outputItem,
        HashSet<string> seenCitations,
        CancellationToken cancellationToken)
    {
        var result = new List<object>();
        var map = TryGetJsonElementMap(outputItem);
        if (map is null)
            return result;

        var itemType = map.TryGetString("type") ?? string.Empty;
        var itemRole = map.TryGetString("role") ?? "assistant";

        if (!string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(itemRole, "assistant", StringComparison.OrdinalIgnoreCase)
            || !map.TryGetValue("content", out var contentElement)
            || contentElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var contentPart in contentElement.EnumerateArray())
        {
            if (!contentPart.TryGetProperty("type", out var typeElement)
                || !string.Equals(typeElement.GetString(), "output_text", StringComparison.OrdinalIgnoreCase)
                || !contentPart.TryGetProperty("annotations", out var annotationsElement)
                || annotationsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var annotation in annotationsElement.EnumerateArray())
            {
                var download = await TryDownloadContainerCitationAsync(annotation, seenCitations, cancellationToken);
                if (download is null)
                    continue;

                AddDownloadArtifacts(result, download);
            }
        }

        return result;
    }

    private async Task<OpenAiContainerCitationDownload?> TryDownloadContainerCitationAsync(
        object annotation,
        HashSet<string> seenCitations,
        CancellationToken cancellationToken)
    {
        var data = TryGetJsonElementMap(annotation);
        if (data is null)
            return null;

        var citationType = data.TryGetString("type");
        if (!string.Equals(citationType, "container_file_citation", StringComparison.OrdinalIgnoreCase))
            return null;

        var containerId = data.TryGetString("container_id");
        var fileId = data.TryGetString("file_id");
        if (string.IsNullOrWhiteSpace(containerId) || string.IsNullOrWhiteSpace(fileId))
            return null;

        return await TryDownloadContainerFileAsync(
            containerId,
            fileId,
            data.TryGetString("filename") ?? data.TryGetString("title"),
            JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web),
            seenCitations,
            cancellationToken);
    }

    private async Task<IReadOnlyList<OpenAiContainerCitationDownload>> TryDownloadAssistantContainerFilesAsync(
        string containerId,
        HashSet<string> seenCitations,
        long turnStartedAtUnixSeconds,
        CancellationToken cancellationToken)
    {
        var files = await TryListAssistantContainerFilesAsync(containerId, cancellationToken);
        if (files.Count == 0)
            return [];

        var downloads = new List<OpenAiContainerCitationDownload>(files.Count);

        foreach (var file in files)
        {
            if (!OpenAiContainerDownloadPolicy.IsFallbackFileFromCurrentTurn(
                    file.CreatedAt,
                    turnStartedAtUnixSeconds))
            {
                continue;
            }

            var download = await TryDownloadContainerFileAsync(
                containerId,
                file.Id,
                string.IsNullOrWhiteSpace(file.Path) ? null : Path.GetFileName(file.Path),
                CreateContainerFileRawData(file),
                seenCitations,
                cancellationToken);

            if (download is not null)
                downloads.Add(download);
        }

        return downloads;
    }

    private async Task<IReadOnlyList<OpenAiContainerFileReference>> TryListAssistantContainerFilesAsync(
        string containerId,
        CancellationToken cancellationToken)
    {
        var result = new List<OpenAiContainerFileReference>();
        string? after = null;

        try
        {
            do
            {
                var requestUri = $"v1/containers/{Uri.EscapeDataString(containerId)}/files?limit=100&order=asc";
                if (!string.IsNullOrWhiteSpace(after))
                    requestUri += $"&after={Uri.EscapeDataString(after)}";

                var page = await _client.GetFromJsonAsync<OpenAiContainerFilesListResponse>(
                    requestUri,
                    JsonSerializerOptions.Web,
                    cancellationToken);

                if (page?.Data is null || page.Data.Count == 0)
                    break;

                foreach (var file in page.Data)
                {
                    if (string.IsNullOrWhiteSpace(file.Id)
                        || !string.Equals(file.Source, "assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(file);
                }

                after = page.LastId;
                if (string.IsNullOrWhiteSpace(after) || page.HasMore != true)
                    break;
            }
            while (true);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task<OpenAiContainerCitationDownload?> TryDownloadContainerFileAsync(
        string containerId,
        string fileId,
        string? filename,
        JsonElement rawData,
        HashSet<string> seenCitations,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(containerId) || string.IsNullOrWhiteSpace(fileId))
            return null;

        var citationKey = OpenAiContainerDownloadPolicy.CreateContainerFileKey(containerId, fileId);
        if (!seenCitations.Add(citationKey))
            return null;

        try
        {
            var canonicalOpenAiFileUrl = $"https://api.openai.com/v1/containers/{containerId}/files/{fileId}/content";

            var containerClient = new ContainerClient(GetKey());
            var content = await containerClient.DownloadContainerFileAsync(containerId, fileId, cancellationToken);
            var bytes = content.Value.ToArray();

            var resolvedFilename = string.IsNullOrWhiteSpace(filename) ? $"{fileId}.bin" : filename;
            var contentTypeProvider = new FileExtensionContentTypeProvider();
            var mediaType = contentTypeProvider.TryGetContentType(resolvedFilename, out var detected)
                ? detected
                : "application/octet-stream";

            var providerMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                [GetIdentifier()] = BuildContainerCitationMetadata(ToJsonElementMap(rawData), containerId, fileId, resolvedFilename, canonicalOpenAiFileUrl)
            };

            return new OpenAiContainerCitationDownload
            {
                ToolCallId = Guid.NewGuid().ToString("n"),
                ContainerId = containerId,
                FileId = fileId,
                Filename = resolvedFilename,
                CanonicalUrl = canonicalOpenAiFileUrl,
                MediaType = mediaType,
                Bytes = bytes,
                DataUrl = ToDataUrl(bytes, mediaType),
                ProviderMetadata = providerMetadata,
                Input = JsonSerializer.SerializeToElement(new
                {
                    file_id = fileId,
                    file_name = resolvedFilename,
                    file_type = mediaType,
                    container_id = containerId,
                    openai_file_url = canonicalOpenAiFileUrl
                }, JsonSerializerOptions.Web)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement CreateContainerFileRawData(OpenAiContainerFileReference file)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "container_file_citation",
            ["container_id"] = file.ContainerId,
            ["file_id"] = file.Id,
            ["filename"] = string.IsNullOrWhiteSpace(file.Path) ? null : Path.GetFileName(file.Path),
            ["path"] = file.Path,
            ["source"] = file.Source,
            ["object"] = file.Object,
            ["bytes"] = file.Bytes,
            ["created_at"] = file.CreatedAt,
            ["raw"] = file.AdditionalProperties
        }, JsonSerializerOptions.Web);

    private void CaptureResponseContainerId(
        ResponseStreamPart update,
        OpenAiResponseStreamEnrichmentState state)
    {
        var containerId = update switch
        {
            ResponseCreated created => TryGetResponseContainerId(created.Response),
            ResponseInProgress inProgress => TryGetResponseContainerId(inProgress.Response),
            ResponseCompleted completed => TryGetResponseContainerId(completed.Response),
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(containerId))
            state.ResponseContainerId = containerId;
    }

    private static string? TryGetResponseContainerId(ResponseResult? response)
    {
        if (response is null)
            return null;

        foreach (var outputItem in response.Output ?? [])
        {
            var outputMap = TryGetJsonElementMap(outputItem);
            var outputContainerId = outputMap?.TryGetString("container_id");
            if (!string.IsNullOrWhiteSpace(outputContainerId))
                return outputContainerId;
        }

        foreach (var tool in response.Tools ?? [])
        {
            var toolMap = TryGetJsonElementMap(tool);
            if (toolMap is null)
                continue;

            var toolType = toolMap.TryGetString("type") ?? string.Empty;
            if (string.Equals(toolType, "code_interpreter", StringComparison.OrdinalIgnoreCase)
                && toolMap.TryGetValue("container", out var codeContainer))
            {
                var codeContainerId = codeContainer.ValueKind switch
                {
                    JsonValueKind.String => codeContainer.GetString(),
                    JsonValueKind.Object => codeContainer.TryGetString("container_id")
                        ?? codeContainer.TryGetString("id"),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(codeContainerId))
                    return codeContainerId;
            }

            if (!string.Equals(toolType, "shell", StringComparison.OrdinalIgnoreCase)
                || !toolMap.TryGetValue("environment", out var environment)
                || environment.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var environmentType = environment.TryGetString("type") ?? string.Empty;
            var containerId = environment.TryGetString("container_id");
            if (string.Equals(environmentType, "container_reference", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(containerId))
            {
                return containerId;
            }
        }

        return null;
    }

    private void AddContainerProviderMetadata(ResponseResult response, string? containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
            return;

        response.Metadata ??= [];
        var providerMetadata = TryConvertObjectMap(
            response.Metadata.TryGetValue("providerMetadata", out var existingProviderMetadata)
                ? existingProviderMetadata
                : null);
        var scopedMetadata = TryConvertObjectMap(
            providerMetadata.TryGetValue(GetIdentifier(), out var existingScopedMetadata)
                ? existingScopedMetadata
                : null);

        scopedMetadata["container"] = new Dictionary<string, object?>
        {
            ["id"] = containerId
        };
        providerMetadata[GetIdentifier()] = scopedMetadata;
        response.Metadata["providerMetadata"] = providerMetadata;
    }

    private static Dictionary<string, object?> TryConvertObjectMap(object? value)
    {
        if (value is Dictionary<string, object?> nullableMap)
            return nullableMap.ToDictionary(entry => entry.Key, entry => entry.Value);

        if (value is Dictionary<string, object> map)
            return map.ToDictionary(entry => entry.Key, entry => (object?)entry.Value);

        try
        {
            var element = value is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
            return element.ValueKind == JsonValueKind.Object
                ? element.Deserialize<Dictionary<string, object?>>(JsonSerializerOptions.Web) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    private async Task<OpenAiGeneratedImageUpload?> TryUploadGeneratedImageToResponseContainerAsync(
        ResponseStreamPart update,
        OpenAiResponseStreamEnrichmentState state,
        CancellationToken cancellationToken)
    {
        if (update is not ResponseOutputItemDone done
            || !string.Equals(done.Item.Type, "image_generation_call", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(done.Item.Id)
            || string.IsNullOrWhiteSpace(state.ResponseContainerId)
            || !state.UploadedGeneratedImageItemIds.Add(done.Item.Id))
        {
            return null;
        }

        var itemProperties = done.Item.AdditionalProperties;
        if (itemProperties is null
            || !itemProperties.TryGetValue("result", out var resultElement)
            || resultElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var base64Result = resultElement.GetString();
        if (string.IsNullOrWhiteSpace(base64Result))
            return null;

        try
        {
            var imageBytes = Convert.FromBase64String(base64Result);
            var outputFormat = itemProperties.TryGetValue("output_format", out var outputFormatElement)
                && outputFormatElement.ValueKind == JsonValueKind.String
                    ? outputFormatElement.GetString()
                    : null;
            var normalizedFormat = NormalizeImageOutputFormat(outputFormat);
            var mediaType = $"image/{normalizedFormat}";
            var filename = $"{done.Item.Id}.{normalizedFormat}";
            var containerId = state.ResponseContainerId;

            var containerClient = new ContainerClient(GetKey());
            var uploadResult = await OpenAIModelExtensions.UploadBytesMultipartAsync(
                containerClient,
                containerId,
                imageBytes,
                mediaType,
                options: new RequestOptions { CancellationToken = cancellationToken });

            var uploadMetadata = ExtractContainerFileMetadata(
                uploadResult,
                containerId,
                filename,
                imageBytes.Length,
                mediaType);

            var canonicalOpenAiFileUrl = string.IsNullOrWhiteSpace(uploadMetadata.FileId)
                ? $"https://api.openai.com/v1/containers/{containerId}/files"
                : $"https://api.openai.com/v1/containers/{containerId}/files/{uploadMetadata.FileId}/content";

            var providerMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                [GetIdentifier()] = new Dictionary<string, object>
                {
                    ["type"] = "container_file_upload",
                    ["tool_name"] = UploadGeneratedImageToolName,
                    ["name"] = UploadGeneratedImageToolName,
                    ["upload_tool"] = true,
                    ["source_item_id"] = done.Item.Id,
                    ["container_id"] = containerId,
                    ["file_id"] = uploadMetadata.FileId,
                    ["filename"] = filename,
                    ["path"] = uploadMetadata.Path,
                    ["bytes"] = uploadMetadata.Bytes,
                    ["media_type"] = mediaType,
                    ["openai_file_url"] = canonicalOpenAiFileUrl,
                    ["raw"] = uploadMetadata.Raw
                }
            };

            return new OpenAiGeneratedImageUpload
            {
                ToolCallId = Guid.NewGuid().ToString("n"),
                SourceItemId = done.Item.Id,
                ContainerId = containerId,
                FileId = uploadMetadata.FileId,
                Filename = filename,
                Path = uploadMetadata.Path,
                CanonicalUrl = canonicalOpenAiFileUrl,
                MediaType = mediaType,
                Bytes = imageBytes,
                ProviderMetadata = providerMetadata,
                Input = JsonSerializer.SerializeToElement(new
                {
                    source_item_id = done.Item.Id,
                    file_name = filename,
                    file_type = mediaType,
                    container_id = containerId
                }, JsonSerializerOptions.Web)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static OpenAiContainerFileUploadMetadata ExtractContainerFileMetadata(
        ClientResult uploadResult,
        string fallbackContainerId,
        string fallbackFilename,
        int fallbackBytes,
        string mediaType)
    {
        try
        {
            var rawContent = uploadResult.GetRawResponse().Content.ToString();
            using var document = JsonDocument.Parse(rawContent);
            var root = document.RootElement.Clone();
            var fileId = root.TryGetString("id") ?? string.Empty;
            var path = root.TryGetString("path");
            var containerId = root.TryGetString("container_id") ?? fallbackContainerId;
            var bytes = root.TryGetNumber("bytes") ?? fallbackBytes;

            return new OpenAiContainerFileUploadMetadata(
                fileId,
                containerId,
                string.IsNullOrWhiteSpace(path) ? $"/mnt/data/{fallbackFilename}" : path,
                bytes,
                root);
        }
        catch
        {
            return new OpenAiContainerFileUploadMetadata(
                string.Empty,
                fallbackContainerId,
                $"/mnt/data/{fallbackFilename}",
                fallbackBytes,
                JsonSerializer.SerializeToElement(new
                {
                    container_id = fallbackContainerId,
                    filename = fallbackFilename,
                    bytes = fallbackBytes,
                    media_type = mediaType
                }, JsonSerializerOptions.Web));
        }
    }

    private static string NormalizeImageOutputFormat(string? outputFormat)
        => (outputFormat ?? "png").Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" => "jpeg",
            "jpeg" => "jpeg",
            "webp" => "webp",
            "gif" => "gif",
            "png" => "png",
            _ => "png"
        };

    private static CallToolResult CreateDownloadToolCallResult(OpenAiContainerCitationDownload download)
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                file_id = download.FileId,
                filename = download.Filename,
                media_type = download.MediaType,
                url = download.CanonicalUrl,
                data_url = download.DataUrl
            }, JsonSerializerOptions.Web)
        };

        if (download.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            result.Content =
            [
                ImageContentBlock.FromBytes(download.Bytes, download.MediaType)
            ];

            return result;
        }

        if (IsTextLikeMediaType(download.MediaType))
        {
            result.Content =
            [
                new EmbeddedResourceBlock
                {
                    Resource = new TextResourceContents
                    {
                        Uri = download.CanonicalUrl,
                        MimeType = download.MediaType,
                        Text = DecodeText(download.Bytes)
                    }
                }
            ];

            return result;
        }

        result.Content =
        [
            new EmbeddedResourceBlock
            {
                Resource = BlobResourceContents.FromBytes(download.Bytes, download.CanonicalUrl, download.MediaType)
            }
        ];

        return result;
    }

    private static CallToolResult CreateGeneratedImageUploadToolCallResult(OpenAiGeneratedImageUpload upload)
        => new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = string.IsNullOrWhiteSpace(upload.FileId)
                        ? $"Uploaded generated image to OpenAI container {upload.ContainerId} at {upload.Path}."
                        : $"Uploaded generated image to OpenAI container {upload.ContainerId} as {upload.FileId} at {upload.Path}."
                }
            ],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                file_id = upload.FileId,
                filename = upload.Filename,
                media_type = upload.MediaType,
                container_id = upload.ContainerId,
                path = upload.Path,
                url = upload.CanonicalUrl,
                source_item_id = upload.SourceItemId
            }, JsonSerializerOptions.Web)
        };

    private static bool IsTextLikeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return false;

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("csv", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeText(byte[] bytes)
        => Encoding.UTF8.GetString(bytes);

    private static string ToDataUrl(byte[] bytes, string? mediaType)
        => $"data:{mediaType ?? "application/octet-stream"};base64,{Convert.ToBase64String(bytes)}";

    private static Dictionary<string, JsonElement> ToJsonElementDictionary(Dictionary<string, object?> source)
        => source.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value, JsonSerializerOptions.Web));

    private sealed class OpenAiContainerCitationDownload
    {
        public string ToolCallId { get; init; } = string.Empty;

        public string ContainerId { get; init; } = string.Empty;

        public string FileId { get; init; } = string.Empty;

        public string Filename { get; init; } = string.Empty;

        public string CanonicalUrl { get; init; } = string.Empty;

        public string MediaType { get; init; } = "application/octet-stream";

        public byte[] Bytes { get; init; } = [];

        public string DataUrl { get; init; } = string.Empty;

        public JsonElement Input { get; init; }

        public Dictionary<string, Dictionary<string, object>> ProviderMetadata { get; init; } = [];
    }

    private sealed class OpenAiGeneratedImageUpload
    {
        public string ToolCallId { get; init; } = string.Empty;

        public string SourceItemId { get; init; } = string.Empty;

        public string ContainerId { get; init; } = string.Empty;

        public string FileId { get; init; } = string.Empty;

        public string Filename { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public string CanonicalUrl { get; init; } = string.Empty;

        public string MediaType { get; init; } = "application/octet-stream";

        public byte[] Bytes { get; init; } = [];

        public JsonElement Input { get; init; }

        public Dictionary<string, Dictionary<string, object>> ProviderMetadata { get; init; } = [];
    }

    private sealed record OpenAiContainerFileUploadMetadata(
        string FileId,
        string ContainerId,
        string Path,
        int Bytes,
        JsonElement Raw);

    private sealed class OpenAiContainerFilesListResponse
    {
        [JsonPropertyName("data")]
        public List<OpenAiContainerFileReference> Data { get; init; } = [];

        [JsonPropertyName("has_more")]
        public bool? HasMore { get; init; }

        [JsonPropertyName("last_id")]
        public string? LastId { get; init; }
    }

    private sealed class OpenAiContainerFileReference
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("container_id")]
        public string? ContainerId { get; init; }

        [JsonPropertyName("object")]
        public string? Object { get; init; }

        [JsonPropertyName("bytes")]
        public int? Bytes { get; init; }

        [JsonPropertyName("created_at")]
        public long? CreatedAt { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }

    private sealed class OpenAiResponseStreamEnrichmentState
    {
        public int WireSequenceNumber { get; set; }

        public List<object> SyntheticOutput { get; } = [];

        public Dictionary<int, int> NativeOutputIndexes { get; } = [];

        public Dictionary<string, int> OutputIndexesById { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SeenCitationKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> UploadedGeneratedImageItemIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> CompletedWebSearchCallIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int CompletedWebSearchCallCount => CompletedWebSearchCallIds.Count;

        public string? ResponseContainerId { get; set; }

        public bool AssistantContainerFallbackAttempted { get; set; }

        public bool DownloadProducedThisTurn { get; set; }

        public long TurnStartedAtUnixSeconds { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public int NextOutputIndex { get; set; }

        public int NextSequenceNumber { get; set; } = 1_000_000;
    }

    private const string DownloadFileToolName = "download_file";

    private const string UploadGeneratedImageToolName = "upload_generated_image";

    private const string UploadFilesToolName = "upload_files";


}
