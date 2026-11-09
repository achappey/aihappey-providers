# aihappey-providers
Reusable **.NET provider implementations** and **shared JSON metadata** for 600+ AI providers.
Native APIs, streaming, tools, media, model catalogs and pricing. 

## Quick start
Call providers directly with your own API keys.
```csharp
using AIHappey.Core.AI;
using AIHappey.ChatCompletions.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

using var services = new ServiceCollection()
    .AddHttpClient()
    .BuildServiceProvider();

var client = new ProviderClient(
    services.GetRequiredService<IHttpClientFactory>());

var keys = new Dictionary<string, string>
{
    ["openai"] = Environment.GetEnvironmentVariable("OPENAI_API_KEY")!
};

var request = JsonSerializer.Deserialize<ChatCompletionOptions>("""
    {"model":"gpt-5.4-mini","messages":[
      {"role":"user","content":"Say hi"}
    ]}
    """)!;

var response = await client.ExecuteAsync(
    "openai", keys,
    (provider, ct) => provider.CompleteChatAsync(request, ct));

Console.WriteLine(JsonSerializer.Serialize(response));
```

## Provider Support Matrix

Below is a capability matrix for all supported providers. 
Provider descriptions and links are available [here](https://chat.aihappey.com/providers).

The table below shows which endpoints each provider implements (✅), not yet implemented (❌), partially implemented (🟡) or for which an endpoint is not applicable to the provider (➖).

💬 [Chat](https://ai-sdk.dev/docs/reference/ai-sdk-ui/use-chat) | ✍️ [ChatCompletions](https://developers.openai.com/api/reference/resources/chat) | 📤 [Responses](https://developers.openai.com/api/reference/resources/responses) | 📨 [Messages](https://platform.claude.com/docs/en/api/messages) | 🖼️ [Images](https://ai-sdk.dev/docs/ai-sdk-core/image-generation) | 🎙️ [Transcriptions](https://ai-sdk.dev/docs/ai-sdk-core/transcription) | 🔊 [Speech](https://ai-sdk.dev/docs/ai-sdk-core/speech) | 🏆 [Rerank](https://ai-sdk.dev/docs/ai-sdk-core/reranking) | 🎬 [Video](https://ai-sdk.dev/docs/ai-sdk-core/video-generation) | 🧩 [Skills](https://developers.openai.com/api/reference/resources/skills) | 🧬 [Embeddings](https://developers.openai.com/api/reference/resources/embeddings) | ⚖️ [Decisions](https://developers.openai.com/api/reference/resources/embeddings) | 📡 [Realtime](https://ai-sdk.dev/docs/ai-sdk-core/realtime)


| Provider | [💬](https://ai-sdk.dev/docs/reference/ai-sdk-ui/use-chat) | [✍️](https://developers.openai.com/api/reference/resources/chat) | [📤](https://developers.openai.com/api/reference/resources/responses) | [📨](https://platform.claude.com/docs/en/api/messages) | [🖼️](https://ai-sdk.dev/docs/ai-sdk-core/image-generation) | [🎙️](https://ai-sdk.dev/docs/ai-sdk-core/transcription) | [🔊](https://ai-sdk.dev/docs/ai-sdk-core/speech) | [🏆](https://ai-sdk.dev/docs/ai-sdk-core/reranking) | [🎬](https://ai-sdk.dev/docs/ai-sdk-core/video-generation) | [🧩](https://developers.openai.com/api/reference/resources/skills) | [🧬](https://developers.openai.com/api/reference/resources/embeddings) | [⚖️](https://developers.openai.com/api/reference/resources/decisions) | [📡](https://ai-sdk.dev/docs/ai-sdk-core/realtime)
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 302AI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ |
| AbacusAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Abliberation | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Allternit | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ampersand | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AddisAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Aether | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ➖ | ✅ |
| AgDev | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Agen | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgencyOSX | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Agent37 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgentAIGateway | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgentCommons | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgentContainer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgentDiscuss | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BaguetteBot | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Jenova | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Agentics | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| AgenticWorkforceME | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgentPhone | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| AgentSky | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgnesAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| AI21 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AiApiWorld | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| AIBadgr | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| AIBramha | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AICC | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ➖ | ✅ | ➖ | ➖ |
| Aichixia | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| AICredits | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| AIgateway | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| AIHorde | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIHubMix | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| AIMagicx | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| AIML | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| AINative | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ainvented | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AionLabs | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIsa | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ✅ | ➖ | ✅ |
| Aivara | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AkashML | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AKI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Akumi | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Alibaba | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ |
| AllToken | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| AlphaNeural | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| AmazonBedrock | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ambient | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Amnt | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Antbase | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Antimatter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Anthropic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Athena | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AnyRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ | ✅ | ➖ |
| AndyAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Apertis | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| ApiAirforce | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ |
| APIPASS | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| APIpie | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| APIPod | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| APIyi | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ | ✅ |
| Apekey | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AppNZ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| ArceeAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ARKLabs | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| ArkRoute | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| ArliAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ARWriter | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| ASIOne | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AskARC | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AskCodi | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AssemblyAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Assisters | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Astica | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Astria | ✅ | 🟡 | 🟡 | 🟡 | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Async | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| AtlasCloud | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Audixa | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Augure | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Auriko | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Avian | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Azerion | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Azure | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ |
| Baidu | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BananaPeel | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BaseAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Baseten | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| BastionGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BazaarLink | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ | ➖ |
| BeastLabAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BergetAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ |
| BespokeLabs | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Bineric | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| BLACKBOX | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BlackForestLabs | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Blink | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ |
| BlockRun | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ | ➖ |
| BotVerse | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Brainiall | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Bria | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Brave | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BrowserUse | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Buildprint | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BytePlus | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| ByteSpace | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Bytez | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Cailos | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CallMissed | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| CAMBAI | ✅ | ✅ | ✅ | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Camelai | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CanopyWave | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CanRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| CARouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Cartesia | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| CaseDev | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ✅ | ✅ |
| Cencori | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Cerebras | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Celeris | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| ChainGPT | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ChainHub | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| ChatQT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Clarifai | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CheaperInference | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| CheapGrok | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Chutes | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ❌ |
| CircuitNotion | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Cirrascale | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Clankie | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ClawHub | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Clauddy | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Claudible | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ClawLite | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Cline | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Clod | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CloudFerro | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Cloudflare | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| CloudRift | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Codebase | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CognitivessAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| Cohere | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Coze | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CometAPI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ➖ | ❌ | ➖ | ➖ |
| CommandCode | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Commonstack | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Concentrate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CondenseChat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Connect0 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Copilot | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CoralBricks | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Cortecs | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| Cortex | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Crazyrouter | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | ❌ | ➖ | ✅ |
| Creora | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Crusoe | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Cursor | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Daglo | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Dandolo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Darkbloom | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Databricks | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| DataForSEO | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DeAPI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| Decart | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| DecisionModels | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| DedalusLabs | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Deepbricks | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DeepInfra | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ |
| DeepL | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DeepSeek | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Deepgram | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Depaza | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DevicAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DigitalOcean | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| DocketRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DocsRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Doubleword | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Dust | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EAGM | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Eachlabs | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| Echo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ecoia | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EdenAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Edgee | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EdgeSpeak | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ElectronHub | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ➖ | ➖ |
| ElevenLabs | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Eliza | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Ellipsis | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EmberCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EmbyAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EmpirioLabsAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Entrim | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Epho | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EuGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Euqai | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EUrouter | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ |
| EvidenceMD | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EverypixelLabs | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| EvoLinkAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Exa | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Featherless | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ✅ | ✅ | ➖ |
| Fal | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ❌ |
| FastRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| Fikra | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Finora | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Fireworks | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ | ✅ | ➖ |
| FishAudio | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| FiveDock | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Flatseek | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Fortytwo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Founden | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Foundry | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Foureverland | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Fred | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| FreeInference | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Freepik | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| FreedomGPT | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Friendli | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| FullAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GateRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GeekAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| GeneralCompute | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Getimg | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| GetGoAPI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GigaChat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Gladia | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Glio | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| GMICloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Google | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ✅ |
| GoogleTranslate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GooseAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GonkaGate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GPTsAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GPTProto | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| Gradium | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| GreenPT | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ | ✅ | ➖ |
| GrooveDev | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Groq | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| GTranslate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GWDG | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Haimaker | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Hanzo | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| HarnessRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HCompany | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Hedra | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Heabsy | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Helicone | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HelyxAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Herma | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Hetzner | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HeyGen | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Hicap | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HiNow | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| HolySheepAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HorayAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HostYourAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ✅ |
| HuggingFace | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | 
| HumanBrowser | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HumeAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Hush | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Hyperbolic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Hyperbrowser | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HyperRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Hyperstack | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| iApp | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ideogram | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| IGPT | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ILMU | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ |
| ImageRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Impossibl | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| InceptionLabs | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Inceptron | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Infercom | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Inferencenet | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| InferLink | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Infermatic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| InferexAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Infomaniak | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Infraxa | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Infron | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Interfaze | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Inworld | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| IOnet | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| IONOS | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Isaacus | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ | ➖ |
| Ishi | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| IonRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| JassieAI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| JiekouAI | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ | ➖ |
| JigsawStack | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Jina | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| JKAIHub | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| JSON2Video | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| JuiceFactory | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| KalpaLabs | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Keln | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Keyplex | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kilo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kimrel | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kirha | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kiosapi | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| KissAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| KlingAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| KnoxChat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ | ➖ |
| Krutrim | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LangbaseAgent | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LangbasePipe | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LaoZhang | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lara | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lazu | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LectoAI | ✅ | ❌ | 🟡 | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LelapaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Leonardo | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LevantoLabs | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| LexiCo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LibertAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ✅ |
| Lightcone | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LightOn | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lilac | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lingvanex | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Linkup | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LitAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LiteRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LovelyRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Llama | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Neuphonic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Mammouth | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| PLaMo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Narakeet | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLM7 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMAPI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| LLMBase | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMGateway | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| LLMkiwi | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMLayer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMStats | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMTR | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ | ➖ |
| LLMWise | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LMRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Logfare | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| LogicosLLMHub | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LongCat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LOVO | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| LTX | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| LucidQuery | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LumaAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Lumecoder | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lumenfall | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| LuminoAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lunos | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Lutheos | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LXG2IT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lyceum | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| M8tes | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MachineFi | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Magisterium | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MancerAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MangoBoost | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Manyfold | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Manus | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MaritacaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Martian | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MatterAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Maxlayer | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ✅ |
| MegaLLM | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MegaNova | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Melious | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ |
| MemoryRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MeraGPT | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Merge | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MeshAPI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| Messari | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Mia21 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MIAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MIMICXAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| MiniMax | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| MindsHub | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ✅ | ➖ |
| MintRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Mireye | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MiroMind | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Mistral | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ✅ |
| Mixedbread | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Mixlayer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MLJunction | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Modal | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ModelMax | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| ModelRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ModelScope | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ModelsLab | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| ModelSync | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MoleAPI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| Monica | ✅ | ✅ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MonsterGaming | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Moonshot | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Morph | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Morpheus | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| MuleRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ | ➖ |
| MuleRun | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MumeAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ |
| Munito | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| MurfAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| MyCoAI | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MyRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ |
| NaceAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| NagaAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| NanoGPT | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ | ➖ |
| Napster | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Nataris | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NavyAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| NEARAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ |
| NLPCloud | ✅ | 🟡 | 🟡 | 🟡 | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Nebius | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Neosantara | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ✅ |
| NetMind | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ✅ |
| NeuralRing | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Neuralwatt | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NexosAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Nextbit | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Nexusify | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NimbleWay | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NinjaChat | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Nodion | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Noiz | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Nolgia | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Nomic | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| NRPNautilus | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Nodebyt | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NONKYCAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Notte | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NousResearch | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Nouswise | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NovAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Novita | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ |
| Nscale | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| NVIDIA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Occludra | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OCRSkill | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Octagon | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OfoxAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OhMyGPT | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Ollama | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Omneity | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OmniaKey | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OneInfer | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| OneKey | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| OODAAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| OnlyPixAI | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| OPEAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| OpenAdapter | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| OpenAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ✅ | ✅ | ✅ |
| OpenCodeGo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenCodeZen | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Openference | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| OpenGate | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenGateway | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenHands | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenLimits | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ |
| OpperAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| OpusCode | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Oraicle | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OrbiGPU | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OrcaRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ |
| OrqAgentRuntime | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OrqRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| Osiris | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OurToken | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OVHcloud | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Parallel | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| ParalonCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Parasail | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PAREL | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Particle | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PaperInstruments | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Paul | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PawaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| PaxaLabs | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| PayPerQ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| Perceptron | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Perplexity | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ✅ | ✅ |
| PiAPI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Picklyone | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| Picsart | ✅ | ✅ | ✅ | ✅ | 🟡 | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Pinecone | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Pioneer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Pixserp | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Plori | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Poe | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Pollinations | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Poolside | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Portkey | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| PreAPI | ❌ | ❌ | ❌ | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Prism | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Puter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PredictionGuard | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ | ➖ |
| PrimeIntellect | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PrunaAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| PublicAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ | ➖ |
| Qiniu | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Quantanova | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| QuiverAI | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Radiance | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Radient | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Railwail | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RampRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Raven | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ |
| RaxAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ReByteModels | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ReByteTasks | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Recraft | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RedPill | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| RegoloAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ |
| ReGraph | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| RekaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Relace | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RelaxAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Renderful | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Replicate | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Requesty | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| ResearchAgent | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ResembleAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Respan | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Respeecher | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Reve | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RewindAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Rime | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| RodiumAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RoutePlex | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Router9 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| RouterPlus | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ | ➖ |
| Routera | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RouterLink | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Routeway | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Routmy | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ✅ |
| Routstr | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Roxy | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RunAPI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Runcrate | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| Runpod | ✅ | ✅ | ❌ | ❌ | ✅ | ❌ | ✅ | ✅ | ➖ | ➖ | ➖ |
| Runtimo | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Runtype | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Runware | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Runway | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| SailResearch | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SambaNova | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Samtal | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ |
| SandBase | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Sapiom | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Sargalay | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Sarvam | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Scaleway | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ |
| ScalixWorld | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SchatziAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ScrapeLLM | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SEALION | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Secrypt | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Segmind | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Select | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SelinaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Sennoric | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SenseNova | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SerenityEdge | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ | ✅ | ➖ |
| Setapp | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Sference | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| ShadowOS | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Shakespeare | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ShannonAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ShareAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Shengsuanyun | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ShuttleAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SiliconFlow | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| SimpleLLM | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Simplismart | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SippulseAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SkypoolToken | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Skyvern | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Sluis | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| SmallestAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| SmartAIPI | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Smooth | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Softref | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Soniox | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| SovereignEG | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Sovinfra | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| SovrGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| SpaceXAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ✅ |
| Spawnlabs | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Speechactors | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Speechify | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Speechmatics | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| StabilityAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| StandardCompute | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| StealthGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| StepFun | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| StreamLake | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| StrikeAI | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| SubQ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| SudoRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| SUFY | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SUMMA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SunbirdAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| SundayPyjamas | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SunoAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| SUPA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SurferCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Surplus Intelligence | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ✅ | ➖ |
| SUTRA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Swarms | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Synexa | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Synthetic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Tapas | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Tavily | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TEAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TeamDay | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Telnyx | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ✅ | ➖ |
| Tembo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TencentHunyuan | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TensorBlock | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TensorX | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| TerminalSkills | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Tetrate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Thalam | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Thaura | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TheOldAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TheRouterAI | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| TextSynth | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Tiarina | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TierUp | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TigerCity | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TikHubAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ❌ |
| Tilde MT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Tinfoil | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TinyFish | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ToAPIs | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| Together | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Token360 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| TokenFlux | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| TokenHub | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TokenLab | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ |
| TokenSpend | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TrueFoundry | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ |
| TrustedRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TTSReader | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| TwelveLabs | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Typecast | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| UltraSafe | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ | ➖ |
| Unbound | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| UncensoredChat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| UncloseAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ❌ | ➖ | ➖ | ➖ | ➖ |
| UniAPI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| UnrealSpeech | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Upstage | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Upivia | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| UVoiceAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Valyu | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| VanceAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Vapi | ✅ | 🟡 | 🟡 | ❌ | ➖ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Venice | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ✅ | ➖ |
| Verbatik | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Vercel | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Verda | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| VIABLELab | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| VibeKit | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Vidu | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Vikasit | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Viktor | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Vivgrid | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| VLMRun | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| VoiceAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Vogent | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Volink | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| VoyageAI | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Vultr | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ |
| Wafer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Waslo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WatsonX | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WAYSCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ |
| WaveSpeed | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WebCrawlerAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WebsearchAPI | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WellSaid | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WiseRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WrenAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Writer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| XALEN | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| XiaomiMIMO | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Xpersona | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Xpectrum | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| xVoice | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Yandex | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Yollomi | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| YouCom | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| YourVoic | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Yutori | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Zai | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Zeabur | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Zebracat | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Zenlayer | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| ZenMux | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ✅ | ✅ | ➖ |
| ZeroEntropy | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Zoom | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| ZyloAPI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Zyphra | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
