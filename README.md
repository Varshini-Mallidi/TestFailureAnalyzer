# TestFailureAnalyzer

> **AI-powered Test Failure Analyzer**

Automatically investigates failed test runs by combining **TRX parsing**, **log + screenshot evidence**, and **RAG-based code retrieval** to generate a clear **HTML investigation report**.

---

## Problem Statement

When a CI pipeline fails, engineers spend time:
- Opening TRX results and searching through logs
- Correlating stack traces with source code
- Checking screenshots 
- Determining if it's a test script bug, app bug, or environment issue
- Summarizing findings for the team

**This tool automates that workflow** and produces a consistent report that helps developers, QA, and PMs understand **what failed**, **why it likely failed**, and **what to do next**.

---

## Key Features

- **TRX Parsing** - Extract failed tests, error messages, stack traces, and metadata
- **Evidence Collection** - Attach relevant logs and screenshots to each failure
- **Smart Classification** - Categorize failures (script/application/environment/uncertain)
- **RAG for Code Context** - Retrieve relevant source code from stack traces using hybrid search (semantic + keyword + stack-trace boosting)
- **Multi-Provider AI** - Gemini (FREE), Azure OpenAI, Ollama
- **HTML Investigation Report** - Readable report with:
  - Root cause analysis with confidence scoring
  - Evidence (TRX + logs + screenshots + source code)
  - Suggested code fixes
  - Next actions

---

##  Tech Stack

- **Language:** C# / .NET 8
- **Test Framework:** MSTest + FlaUI (Windows UI Automation)
- **AI Providers:** Gemini,Azure OpenAI,Ollama
- **Retrieval:** RAG (embeddings + vector store + hybrid search)
- **Output:** HTML report with detailed analysis

---

## Getting Started

### Prerequisites
- .NET 8.0 SDK
- Test results (`.trx` file) , logs and screenshots from your test run
- AI provider API key (Gemini is FREE: [Get key](https://aistudio.google.com/app/apikey))

### Quick Start

#### 1. Clone the repo
```bash
git clone https://github.com/Varshini-Mallidi/TestFailureAnalyzer.git
cd TestFailureAnalyzer
```

#### 2. Add your API key
Edit `appsettings.json`:
```json
{
  "Gemini": {
	"ApiKey": "YOUR-GEMINI-API-KEY",
	"Model": "gemini-2.0-flash-exp",
	"MaxOutputTokens": 8192
  }
}
```

#### 3. Run analysis
```bash
dotnet run 
  --trx path/to/results.trx \
  --logs path/to/logs \
  --source-dir path/to/source/code \
  --gemini \
  --output report.html
```

#### 4. View the report
Open `report.html` in your browser to see:
- Root cause classification (script/app/environment)
- Confidence score with evidence summary
- Suggested fix with code snippets
- Timeline and screenshot analysis

---

##  Usage Examples

### Basic Analysis
```bash
dotnet run 
  --trx TestResults/results.trx \
  --logs TestResults/logs \
  --source-dir "C:\Users\YourUsername\Projects\SourceRepo" \
  --gemini \
  --output report.html
```

### With Source Code Context (RAG)
**Step 1:** Index your test code (one-time)
```bash
dotnet run --project FailureAnalyzer -- \
  --index \
  --source-dir "C:\Projects\MyTests"
```

**Step 2:** Analyze with code context
```bash
dotnet run --project FailureAnalyzer -- \
  --trx TestResults/results.trx \
  --logs TestResults/logs \
  --source-dir "C:\Projects\MyTests" \
  --gemini \
  --output report.html
```
**Benefit:** Get fixes that match your actual coding patterns and page object models.

### Azure DevOps Integration
**Configure** `appsettings.json`:
```json
{
  "AzureDevOps": {
	"OrganizationUrl": "https://dev.azure.com/your-org",
	"ProjectName": "YourProject",
	"PersonalAccessToken": "your-pat-token",
	"DefaultPipelineId": 123
  }
}
```

**Run:**
```bash
dotnet run --project FailureAnalyzer -- --ado-latest --gemini
```
This auto-fetches the latest pipeline results and generates a report.

---

##  Project Structure

```
FailureAnalyzer/
├── Commands/              # CLI command handlers
├── Configuration/         # Config models (RAG, ADO)
├── Models/                # Domain models (test results, evidence, analysis)
├── Reports/               # HTML report generator
├── Services/
│   ├── Analysis/          # Failure analysis parsing
│   ├── Evidence/          # TRX, logs, screenshots, stack traces
│   ├── Integration/       # Azure DevOps client
│   ├── Providers/         # AI provider implementations (Gemini, Azure OpenAI, etc.)
│   └── RAG/               # Code retrieval (chunking, indexing, hybrid search)
├── Tests/                 # Unit tests
├── Utils/                 # Helper utilities
├── Program.cs             # Main orchestration
└── appsettings.json       # Configuration (API keys, endpoints)
```

---

## CLI Options

### Required (choose one)
- `--trx <path>` - Path to TRX test results file
- `--ado-latest` - Auto-fetch latest from Azure DevOps

### AI Provider (choose one)
- `--gemini` - Google Gemini (FREE)
- `--azure` - Azure OpenAI
- `--openai` - OpenAI
- `--ollama` - Local Ollama

### Optional
- `--logs <path>` - Log files directory
- `--source-dir <path>` - Source code for RAG
- `--screenshots <path>` - Screenshot directory
- `--output <path>` - Report output path (default: `report.html`)
- `--index` - Run code indexing only
- `--force-index` - Force rebuild vector index
- `--markdown` - Generate Markdown report
- `--debug` - Verbose logging

---

- **Gemini API:** [Get Free Key](https://aistudio.google.com/app/apikey)

---

## Use Cases

### 1. Daily Test Failure Triage
**Before:** Engineers manually review 10-20 failed tests each morning  
**After:** Run analyzer overnight, review HTML reports with root causes and fixes

### 2. CI/CD Pipeline Integration
**Before:** Pipeline fails, engineers dig through logs manually  
**After:** Pipeline publishes AI-generated failure report as artifact

### 3. Team Knowledge Sharing
**Before:** Only senior engineers can diagnose complex failures  
**After:** Junior engineers use reports to understand failure patterns

### 4. Failure Pattern Analysis
**Before:** No visibility into recurring failure types  
**After:** Track classification trends (script vs app vs environment)

---
