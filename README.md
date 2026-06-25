# FailureAnalyzer

AI-powered test failure analysis for MSTest + FlaUI using Azure OpenAI.

## What it does

Reads your `.trx` file after a test run, sends each failure to Azure OpenAI (GPT-4o),
and generates an HTML + Markdown report with:

- Root cause per failure (locator / timing / environment / data / app_crash)
- Contributing factors
- Prioritized fix suggestions with C# code snippets
- Cross-cutting patterns across all failures

## Setup

### 1. Azure OpenAI resource

In Azure Portal:
1. Create an **Azure OpenAI** resource in your subscription
2. Deploy a model — use `gpt-4o` (recommended) or `gpt-4-turbo`
3. Copy the **Endpoint** and **API Key** from Keys and Endpoint

### 2. Configure locally

Edit `appsettings.json`:

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://YOUR-RESOURCE.openai.azure.com/",
    "ApiKey":   "YOUR-KEY",
    "DeploymentName": "gpt-4o"
  }
}
```

### 3. Run locally

```bash
cd FailureAnalyzer
dotnet run -- \
  --trx path/to/results.trx \
  --logs path/to/log-directory \
  --output report.html \
  --env "Local" \
  --context "Testing fix for ADO-1234"
```

Open `report.html` in a browser.

### 4. Azure DevOps pipeline

1. Copy `azure-pipelines.yml` to your repo root
2. In ADO: **Pipeline > Edit > Variables**
   - Add `AZUREOPENAI_API_KEY` → your key → ✅ Mark as secret
   - Add `AZUREOPENAI__ENDPOINT` → your endpoint URL
3. Push and run — find the report under **Artifacts > failure-analysis**

## CLI flags

| Flag | Default | Description |
|------|---------|-------------|
| `--trx` | *(required)* | Path to .trx file |
| `--logs` | same dir as TRX | Directory with .log / .txt files |
| `--output` | `failure-report.html` | Output file path |
| `--env` | `Azure DevOps CI` | Environment name shown in report |
| `--context` | *(none)* | Extra context for AI (branch, ticket, recent changes) |
| `--max-failures` | `15` | Max failures to analyze (cost control) |
| `--fail-on-critical` | `true` | Exit code 2 if critical failures found |

## Cost

Approx. **$0.05–0.15 per test run** at typical log sizes with GPT-4o.
For 50 tests with ~10 failures: expect < $0.10 per run.

## Project structure

```
FailureAnalyzer/
├── Program.cs                     # CLI entry point
├── appsettings.json               # Config (don't commit API keys)
├── azure-pipelines.yml            # ADO pipeline integration
├── Models/
│   └── Models.cs                  # TestResult, FailureAnalysis, CliOptions
├── Services/
│   ├── TrxParser.cs               # Parses .trx XML into C# objects
│   ├── LogReader.cs               # Reads and chunks log files
│   └── AzureOpenAIAnalyzer.cs     # Azure OpenAI API calls + prompts
└── Reports/
    └── HtmlReportGenerator.cs     # Generates HTML + Markdown reports
```

## Swapping AI provider

The AI layer is isolated in `AzureOpenAIAnalyzer.cs`. To switch:
- **OpenAI directly**: replace `Azure.AI.OpenAI` client init with `OpenAIClient(apiKey)`
- **Ollama**: replace `GetChatCompletionsAsync` with an `HttpClient` POST to `http://localhost:11434/api/chat`

The prompts and response parsing stay identical.
