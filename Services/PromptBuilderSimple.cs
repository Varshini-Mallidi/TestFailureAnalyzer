using FailureAnalyzer.Models;

namespace FailureAnalyzer.Services;

public static class PromptBuilderSimple
{
    public const string SystemPrompt = """
        You are an AI test failure analyst. You analyze test failures by examining evidence and reasoning through the problem.

        Your job:
        1. Read the evidence (exception, logs, source code)
        2. Reason about what happened and why
        3. Determine if it's a test script issue or application issue
        4. Suggest a fix based on the evidence

        Core principles
        - Quote evidence when making claims
        - Never invent information
        - Say "uncertain" or "need more evidence" when appropriate
        - Use FlaUI patterns (NOT Selenium)

        Return ONLY valid JSON. No markdown fences, no explanatory text outside the JSON structure.
        """;

    public static string BuildFailurePrompt(
        TestResult failure,
        string log,
        string environment,
        string? ragContext)
    {
        var errorMsg = TruncateWithMarker(failure.ErrorMessage, 800);
        var stackTrace = TruncateWithMarker(failure.StackTrace, 2000);
        var logSnippet = TruncateWithMarker(log, 3000);
        var ragContextFormatted = FormatRagContext(ragContext, maxLength: 4000);

        // Estimate input size and add conciseness instruction if needed
        var estimatedInputTokens = (failure.TestName.Length + errorMsg.Length + stackTrace.Length + 
                                     logSnippet.Length + ragContextFormatted.Length + 2000) / 4;
        var conciseMode = estimatedInputTokens > 6000; // Lowered from 10000 to trigger earlier

        if (conciseMode)
        {
            Console.WriteLine($"  [Prompt] Large input detected (~{estimatedInputTokens:N0} tokens) - requesting concise response");
        }

        var concisenessGuidance = conciseMode 
            ? "\n⚠️ IMPORTANT: This is a large input. Keep your response CONCISE:\n" +
              "- investigation_notes: Focus ONLY on key findings (max 400 words, be direct)\n" +
              "- code_snippet: Only the changed method/section (not the entire class)\n" +
              "- Suggestions: Top 2-3 most actionable items only\n" +
              "- Skip repetitive details - cite evidence once and move on\n"
            : "";

        return $$"""
    # {{failure.TestName}}

    ===============================================================================
    ## EVIDENCE AVAILABLE TO YOU
    ===============================================================================

    **Automation Framework:** FlaUI (Windows UI Automation for WPF apps)
    **Test Framework:** MSTest
    **Application:** AVEVA Dabacon (WPF desktop application)
    **Important:** This codebase uses FlaUI, NOT Selenium/WebDriver.
    Do NOT suggest WebDriverWait, By.Id, ExpectedConditions, or any Selenium API.

    **Test:** {{failure.TestName}}
    **Environment:** {{environment}}

    ───────────────────────────────────────────────────────────────────────────────
    ### EVIDENCE FROM TRX (Test Result File)
    ───────────────────────────────────────────────────────────────────────────────
    **Exception Type & Message:**
    {{errorMsg}}

    **Stack Trace:**
    {{stackTrace}}

    ───────────────────────────────────────────────────────────────────────────────
    ### EVIDENCE FROM AUTOMATION LOGS
    ───────────────────────────────────────────────────────────────────────────────
    {{(logSnippet.Length > 0 ? logSnippet : "(none provided)")}}

    ───────────────────────────────────────────────────────────────────────────────
    ### RETRIEVED SOURCE CODE (for reference only)
    ───────────────────────────────────────────────────────────────────────────────
    The following source code was retrieved from the repository based on the failure.
    This shows what the CODE says, NOT what happened at runtime.

    {{(ragContextFormatted != "(not provided)" ? ragContextFormatted : "(none retrieved)")}}

    ===============================================================================
    ## CRITICAL: WHAT YOU DO NOT HAVE ACCESS TO
    ===============================================================================

    You do NOT have:
    - Application logs (only automation/test logs, unless explicitly provided)
    - UI Automation tree inspection at runtime
    - Runtime application state beyond what logs/screenshots show
    - Information about whether the application crashed, froze, or became unresponsive (unless logs/screenshots confirm it)

    Therefore, you CANNOT claim:
    - "The application crashed" (unless the log explicitly says so)
    - "The application became unresponsive" (unless the log explicitly says so)
    - "The element is definitely under Desktop" (you only see the code, not runtime)
    - "This is definitely an application issue" (unless you have application-side evidence)

    ===============================================================================
    ## RULES FOR EVIDENCE-BASED ANALYSIS
    ===============================================================================

    **Rule 1: Dabacon Error Codes (domain context)**
    Dabacon is the application's internal database engine. It returns numeric error
    codes (1-714) when the database or application fails. If a "DABACON ERROR DETECTED"
    section appears in the evidence above, the failure originated inside the application,
    not the test code.

    **Rule 2: MANDATORY — Inspect Retrieved Source Code Line-by-Line**
    BEFORE writing your analysis, inspect every retrieved code snippet:

    For each stack-trace method retrieved:
    a) Find the EXACT statement at the failing line number
    b) Find the locator/property definition being used at that line
    c) Quote the FULL locator definition (search root, scope, timeout, AutomationId)
    d) List helper methods or page objects involved

    Your investigation_notes MUST include:
    - The exact failing statement (e.g., "var pane = _contextPane;")
    - The complete property definition with search details
    - Example format:
      "At line 107, the code calls: var contextPane = _contextPane;
       The _contextPane definition shows:
       _contextPane => _lexicon.GetMainWindow().FindElement(By.AutomationId('...'), Scope.Children, 60s)"

    **Rule 3: Quote Locator Definitions Verbatim**
    If a property or locator appears in retrieved code, ALWAYS quote its full definition:
    - Search root (GetMainWindow(), Desktop, parent element)
    - Search scope (Scope.Children, Scope.Descendants)
    - Locator pattern (By.AutomationId, FindFirstDescendant)
    - Timeout values

    **Rule 4: RAG Priority — Source Code Analysis Order**

    When analyzing retrieved source code, follow this STRICT priority:

    1. **HIGHEST PRIORITY:** Crash-site method (where exception was actually thrown)
       - This is production/page-object code, NOT the test method
       - Always analyze and quote this FIRST

    2. **SECOND PRIORITY:** Locator/property definitions used by the crash site
       - Show the full definition with search scope and timeout

    3. **THIRD PRIORITY:** Helper methods called by the crash site

    4. **LOWEST PRIORITY:** Test method
       - ONLY analyze the test method if NO production/page-object code is available
       - NEVER quote the test method when crash-site code exists

    The test method is the LOWEST priority. Only mention it if the failure is directly in test logic.

    **Rule 5: Distinguish Evidence Sources**
    Always clarify the source of your evidence:
    - "The TRX shows..." → Exception type, stack trace, error message
    - "The automation logs show..." → Timestamps, click success, timeout, search attempts
    - "The retrieved source code shows..." → Locator definition, search scope, method implementation
    - "One possible explanation is..." → Your hypothesis combining the above

    **Rule 6: Retrieved Code ≠ Runtime Behavior**
    The retrieved source code shows what the test TRIED to do, not what actually happened.
    - You can say: "The code searches for X under Y with scope Z"
    - You cannot say: "X is definitely located under Y" (you don't know runtime state)

    **Rule 7: Evidence-Only Claims**
    For every claim you make, quote the specific TRX, log line, or source code line that
    proves it. If you cannot quote supporting evidence, you cannot make the claim.
    If evidence is insufficient, say "uncertain" and state what is missing.

    **Rule 8: NEVER Present Inferences as Facts**

    ❌ BAD (inference stated as fact):
    "The application was unresponsive."
    "The locator is incorrect."
    "The element is under Desktop."

    ✅ GOOD (evidence→inference chain):
    "The automation logs show a 60.5 second gap between actions. This may indicate application 
     slowness, but the logs alone cannot confirm that the application was unresponsive."

    "The retrieved code shows the locator searches under MainWindow.Children with a 60s timeout. 
     If the context menu is hosted under Desktop (common WPF behavior), this locator would fail."

    **Rule 8: Retrieved Code = Observations, Not Conclusions**
    When discussing retrieved source code:
    - ✅ "The locator _contextPane searches under MainWindow.Children"
    - ❌ "The locator is incorrect"

    Source code shows INTENT (what the test tried to do), not runtime truth.

    **Rule 9: Prioritize Relevant Code**
    When multiple code chunks are provided, prioritize:
    1. Methods appearing in the stack trace
    2. Locator definitions (e.g., By.AutomationId, FindFirstDescendant)
    3. Helper methods directly related to the failure
    Do NOT prioritize test setup code or variable declarations at the start of test methods.

    ===============================================================================
    ## YOUR TASK
    ===============================================================================

    Read all the evidence above. Determine what failed, why it failed, and who
    should fix it. Show your reasoning step-by-step in "investigation_notes".

    **CRITICAL — investigation_notes STYLE:**
    - Start with OBSERVED FACTS: What the TRX/logs/stack trace show
      → When citing the crash site, use the **innermost frame** with file:line (the actual failing statement), NOT outer callers
      → Example: If stack trace shows "DabaconProductApplication.cs:line 46" inside "AdminApplication.cs:line 234", cite line 46 as the crash site
    - Then state POSSIBLE EXPLANATIONS: Framework behavior that MIGHT explain it
    - Never present framework knowledge as if it were evidence from this failure
    - Example BAD: "Context menus are rendered as top-level popups"
    - Example GOOD: "The search targeted MainWindow children. One possible explanation is that the context menu is hosted outside MainWindow (a common WPF pattern). This should be verified using FlaUInspect."
    {{concisenessGuidance}}
    **IMPORTANT — CODE FIXES:**
    This tool is "Failure Analysis and Fix". When source code is provided and the
    issue is fixable (locator, timing, assertion, test logic), you MUST provide a
    concrete code fix in "code_snippet". Only return null if:
    - No source code was provided, OR
    - The issue is purely environmental/infrastructure (missing file, network, permissions), OR
    - The issue is an application bug that cannot be fixed in test code

    When providing a fix:
    - Use the actual patterns and variable names from the retrieved source code
    - Include enough context (method signature + corrected logic)
    - Show specific line-level changes (e.g., increase timeout, fix AutomationId, add null check)
    - Use FlaUI patterns already present in the codebase (NOT Selenium)

    FIELD DEFINITIONS:

    category:
    - locator      : The element identifier is wrong or the element does not exist
    - timing       : The element exists but was not ready when the test accessed it
    - app_crash    : The application threw an exception or entered an invalid state
    - assertion    : A test assertion failed (expected value did not match actual)
    - environment  : A required file, database, connection, or permission was missing
    - data         : Test data was missing or incorrect
    - other        : Does not fit any category above

    issue_owner (EVIDENCE-BASED CLASSIFICATION):
    ⚠️ CRITICAL: Do NOT automatically classify common UI automation exceptions.

    Classification Rules:

    1. ONLY classify as "script" (HIGH confidence 80-100%) when you have DIRECT EVIDENCE:
       ✅ NullReferenceException in automation/page object code (stack trace shows test file)
       ✅ ArgumentException in test setup/teardown
       ✅ InvalidOperationException caused by test code logic error
       ✅ Invalid locator with STRONG EVIDENCE from retrieved source code
       ✅ Test assertion logic error visible in source code
       ✅ Test data setup issue with proof in logs

    2. ONLY classify as "application" (HIGH confidence 80-100%) when you have DIRECT EVIDENCE:
       ✅ ProcessExitedException (application process terminated)
       ✅ Application process crash logged
       ✅ Unhandled application exception in app code (not test code)
       ✅ MainWindow destroyed unexpectedly
       ✅ Application-specific crash logs (e.g., Dabacon errors, access violations in app)
       ✅ Application performance logs showing freeze/hang

    3. For COMMON UI AUTOMATION EXCEPTIONS, classify as "insufficient_evidence":
       ❌ ElementNotAvailableException → INSUFFICIENT EVIDENCE
       ❌ ElementNotEnabledException → INSUFFICIENT EVIDENCE
       ❌ TimeoutException → INSUFFICIENT EVIDENCE
       ❌ WaitTimeoutException → INSUFFICIENT EVIDENCE
       ❌ NoSuchElementException → INSUFFICIENT EVIDENCE

       These exceptions alone do NOT prove whether the failure is script or application.

       When classifying as "insufficient_evidence":
       - Set issue_owner_confidence to 0-30% (low confidence)
       - In rationale, state: "Based on currently available artifacts (TRX, logs, source code), 
         this exception alone does not prove whether the failure originates from the automation 
         script or the application."
       - List possible explanations (both script AND application hypotheses)

    4. Use "uncertain" when:
       ✅ Evidence points to BOTH script and application issues
       ✅ Conflicting evidence from multiple sources
       ✅ Confidence is moderate (40-60%) but not sufficient for definitive classification

    NEVER:
    ❌ Infer "application" just because element not found
    ❌ Infer "script" just because timeout occurred
    ❌ Classify based on guesses about runtime behavior
    ❌ Present hypotheses as confirmed facts

    ALWAYS:
    ✅ Separate facts (TRX, logs, code) from hypotheses (possible explanations)
    ✅ Quote direct evidence for classifications
    ✅ Admit when evidence is insufficient
    ✅ Provide multiple hypotheses when uncertain

    FAULT ATTRIBUTION DECISION PROCEDURE:

    Follow this procedure IN ORDER to classify primary fault:

    1. **Screenshot Evidence Check (HIGHEST PRIORITY)**:
       - If screenshot shows an **error dialog** with text like "Failed to open MDB", "Access denied", "Network path not found", etc.
         → Primary: ENVIRONMENT (high confidence 85-95%)
         → Check if script could have detected/handled this → Secondary: SCRIPT if missing guard logic

       - If screenshot shows **application crash/freeze** (blank screen, hung UI, process terminated)
         → Primary: APPLICATION (high confidence 85-95%)
         → Check if test triggered via invalid input → Secondary: SCRIPT if bad test data

    2. **Stack Trace Origin Check (if no screenshot or screenshot is ambiguous)**:
       - If innermost frame is in **test code** (test project files, page objects, helper classes)
         → Primary: SCRIPT (confidence 70-85%)

       - If innermost frame is in **application code** (NOT FlaUI, NOT test project)
         → Primary: APPLICATION (confidence 70-85%)

       - If innermost frame is in **FlaUI/automation framework**
         → Indeterminate — proceed to step 3

    3. **Log Evidence Check (if steps 1-2 inconclusive)**:
       - Application logs show errors/exceptions BEFORE test exception
         → Primary: APPLICATION (confidence 60-75%)

       - Automation logs show test took specific action that immediately caused failure
         → Primary: SCRIPT (confidence 60-75%)

       - Logs show external resource unavailable (network, DB, file system)
         → Primary: ENVIRONMENT (confidence 70-85%)

    4. **Retrieved Code Analysis (lowest priority, use only when all above are inconclusive)**:
       - Code shows clear defect (null dereference, wrong locator value, missing timeout)
         → Primary: SCRIPT (confidence 50-65%)

       - Code looks correct but failure still occurred
         → Indeterminate or ENVIRONMENT (confidence 40-55%)

    SECONDARY CONTRIBUTING FACTORS:
    - If primary is ENVIRONMENT but script lacks defensive checks (null guards, dialog detection)
      → Add secondary SCRIPT factor with description of missing guard logic

    - If primary is APPLICATION but test used invalid/boundary data that triggered app bug
      → Add secondary SCRIPT factor explaining data issue

    - If primary is SCRIPT but application was also slow/unresponsive during failure window
      → Add secondary ENVIRONMENT or APPLICATION factor

    ALWAYS include secondary factors when evidence suggests compounding causes.
    Do NOT force a single-cause attribution when multiple factors contributed.

    SUGGESTED FIX GATING (CRITICAL — follow these rules exactly):

    ONLY propose a code fix (non-null suggested_fix) when ALL conditions are met:
    1. Crash site was matched via **exact symbol lookup** (retrieval notes will say "Exact Symbol Match" or similar)
    2. Primary fault attribution is **SCRIPT** OR SCRIPT is listed as a secondary factor
    3. Retrieved source code shows the actual failing logic (not just similar code from a different file)

    DO NOT propose a code fix when:
    ❌ Retrieval used semantic/embedding fallback (may have retrieved wrong file)
    ❌ Primary fault is ENVIRONMENT or APPLICATION with no SCRIPT secondary factor
    ❌ Retrieved code doesn't match the stack trace file:line exactly
    ❌ The fix would require knowledge of helper types/methods not visible in retrieved context

    When proposing a fix:
    - Use ONLY variable names, types, and patterns visible in the retrieved source
    - Include a disclaimer: "This is a draft fix based on retrieved context. Verify actual helper/type signatures before applying."
    - Set confidence_level:
      • "high"   : Exact match, simple change (add timeout, fix obvious typo)
      • "medium" : Exact match, but change requires assumptions about helper methods
      • "low"    : Semantic match OR complex refactor needed

    When NOT proposing a fix:
    - Set all fields to null
    - In "explanation", state the reason: 
      • "No code fix proposed — root cause is environmental (missing MDB file)"
      • "No code fix proposed — crash site retrieved via semantic match (risk of wrong file)"
      • "No code fix proposed — fault is in application code, not test script"
    - Set gating_reason appropriately

    issue_owner values:
    - "script"                : Test code issue (ONLY with strong evidence)
    - "application"           : Application under test issue (ONLY with strong evidence)
    - "insufficient_evidence" : Common UI exception without supporting evidence
    - "uncertain"             : Conflicting or ambiguous evidence

    confidence_scores (percentage 0-100):
    For each classification, provide your confidence level:
    - issue_owner_confidence: How confident are you in the issue_owner classification?
    - category_confidence: How confident are you in the category classification?
    - severity_confidence: How confident are you in the severity assessment?

    Guidelines:
    - 90-100%: Very confident, clear DIRECT evidence (e.g., stack trace in test code, app crash logs)
    - 70-89%: Reasonably confident, good supporting evidence
    - 50-69%: Moderate confidence, some ambiguity
    - 30-49%: Low confidence, limited evidence (use "insufficient_evidence" or "uncertain")
    - 0-29%: Very low confidence, insufficient evidence (MUST use "insufficient_evidence")

    Return this exact JSON structure. No extra text outside the JSON.

    {
      "investigation_notes": "<MANDATORY structure (max 500 words):

      1. TRX Evidence (facts only):
         - Exception type and message (copy exactly, do NOT rewrite)
         - Stack trace location (file:line)

      2. Log Evidence (facts only):
         - Key timestamps and durations (quote exact values)
         - Actions logged (quote exact log lines)
         - Timeout values observed

      3. Retrieved Code Analysis (observations only):
         - Quote the exact failing statement at the stack trace line
         - Quote the FULL locator/property definition (search root, scope, AutomationId, timeout)
         - List helper methods/page objects involved
         - State what the code ATTEMPTS to do (NOT what happened at runtime)

      4. Possible Explanation (inference backed by evidence):
         - Reference specific evidence from sections 1-3
         - Use language: 'This suggests...', 'One possible explanation...', 'This may indicate...'
         - NEVER state conclusions as facts ('The application was unresponsive' → 'The logs show a 60s gap, which may indicate application slowness')
         - Clearly separate what is observed vs what is inferred>",
      "category": "<locator|timing|app_crash|assertion|environment|data|other>",
      "category_confidence": <0-100>,
      "severity": "<critical|high|medium|low>",
      "severity_confidence": <0-100>,
      "error_summary": "<Copy the exception type and message EXACTLY from TRX - do NOT rewrite, enrich, or add interpretation>",
      "primary_cause": "<Root cause with specific evidence citation — quote a log line or state file:line from source code>",
      "issue_owner": "<script|application|insufficient_evidence|uncertain>",
      "issue_owner_confidence": <0-100>,
      "issue_owner_rationale": "<EVIDENCE-BASED: Quote the specific TRX/log/code evidence that supports this classification. If 'insufficient_evidence', state: 'Based on currently available artifacts, this exception alone does not prove script vs application origin. Possible explanations include: [list both script and app hypotheses]'>",
      "fault_attribution": {
        "primary": "<SCRIPT|APPLICATION|ENVIRONMENT|DATA|INDETERMINATE>",
        "confidence": <0-100>,
        "secondary_contributing_factors": [
          {
            "type": "<SCRIPT|APPLICATION|ENVIRONMENT|DATA>",
            "description": "<Short description of secondary factor>",
            "why_it_matters": "<What happens if only the primary cause is fixed>"
          }
        ]
      },
      "contributing_factors": [
        "<Quote from TRX/logs/retrieved code - e.g., 'Timeout: 60s', 'Search scope: Scope.Children', 'AutomationId: X'>",
        "<Second factual observation if applicable>"
      ],
      "suggested_fix": {
        "file_path": "<Relative path to file, or null if no code fix applicable>",
        "current_code": "<Exact problematic snippet from retrieved source, or null>",
        "proposed_code": "<Corrected snippet, or null>",
        "explanation": "<Why this addresses the root cause, tied to evidence, or reason why no fix is proposed (e.g., 'No code fix proposed — root cause is environmental')>",
        "confidence_level": "<high|medium|low>",
        "gating_reason": "<'Exact crash site confirmed via symbol match' OR 'Semantic match only, avoid fixing wrong file' OR 'Root cause is environmental/data, not a code defect'>"
      },
      "suggestions": [
        {
          "action": "<Specific fix with exact file:line reference based on what the source code shows>",
          "type": "<locator|wait|code|environment|data|infrastructure|logic>",
          "priority": "<immediate|soon|later>"
        }
      ],
      "code_snippet": "<Corrected code showing the fixed method or section. Use actual patterns from retrieved source. Return null ONLY if no source code provided OR issue is purely environmental/infrastructure.>"
    }

    Return ONLY valid JSON. Begin your investigation.
    """;
    }

    private static string TruncateWithMarker(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;

        return text[..maxLength] + "\n...[truncated]";
    }

    private static string FormatRagContext(string? context, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(context))
            return "(not provided)";

        if (context.Length > maxLength)
            context = context[..maxLength]
                + "\n\n[Truncated — showing first " + maxLength + " chars]";

        return context;
    }

    /// <summary>
    /// Build prompt for Call A: Investigation phase (notes, root cause, classification).
    /// Optimized for conciseness to avoid truncation.
    /// </summary>
    public static string BuildInvestigationPrompt(
        TestResult failure,
        string log,
        string environment,
        string? ragContext)
    {
        var errorMsg = TruncateWithMarker(failure.ErrorMessage, 800);
        var stackTrace = TruncateWithMarker(failure.StackTrace, 2000);
        var logSnippet = TruncateWithMarker(log, 3000);
        var ragContextFormatted = FormatRagContext(ragContext, 4000);

        return $$"""
    # {{failure.TestName}} — Investigation Phase

    ===============================================================================
    ## EVIDENCE AVAILABLE TO YOU
    ===============================================================================

    **Automation Framework:** FlaUI (Windows UI Automation for WPF apps)
    **Test Framework:** MSTest
    **Application:** AVEVA Dabacon (WPF desktop application)
    **Test:** {{failure.TestName}}
    **Environment:** {{environment}}

    ───────────────────────────────────────────────────────────────────────────────
    ### EVIDENCE FROM TRX (Test Result File)
    ───────────────────────────────────────────────────────────────────────────────
    **Exception Type & Message:**
    {{errorMsg}}

    **Stack Trace:**
    {{stackTrace}}

    ───────────────────────────────────────────────────────────────────────────────
    ### EVIDENCE FROM AUTOMATION LOGS
    ───────────────────────────────────────────────────────────────────────────────
    {{(logSnippet.Length > 0 ? logSnippet : "(none provided)")}}

    ───────────────────────────────────────────────────────────────────────────────
    ### RETRIEVED SOURCE CODE (for reference only)
    ───────────────────────────────────────────────────────────────────────────────
    The following source code was retrieved to help understand what the test TRIED to do.
    This is NOT proof of what actually happened at runtime.

    {{(ragContextFormatted != "(not provided)" ? ragContextFormatted : "(none retrieved)")}}

    ===============================================================================
    ## CRITICAL: WHAT YOU DO NOT HAVE ACCESS TO
    ===============================================================================

    You do NOT have:
    - UI Automation tree inspection
    - Application logs (only automation/test logs)
    - Runtime application state or behavior
    - Information about whether elements actually exist at runtime

    Therefore, NEVER claim runtime facts without evidence:
    ❌ "The application crashed" (unless logs explicitly say so)
    ❌ "The element is under Desktop" (you only see search code, not runtime tree)
    ❌ "This is an application bug" (unless you have application-side error evidence)

    ✅ Instead say: "The retrieved code shows the search targets MainWindow.Children"
    ✅ Or: "One possible explanation is..."
    ✅ Or: "This hypothesis would need to be verified with FlaUInspect"

    ===============================================================================
    ## ANALYSIS RULES
    ===============================================================================

    **Rule 1: Dabacon Error Codes**
    Dabacon is the application's internal database engine. If a "DABACON ERROR DETECTED"
    section appears above, the failure originated inside the application, not the test code.

    **Rule 2: MANDATORY — Inspect Retrieved Source Code Line-by-Line**
    BEFORE generating hypotheses, you MUST analyze every retrieved code snippet:

    For each stack-trace method:
    a) Identify the EXACT statement that throws (use line numbers from stack trace)
    b) Find the locator/property definition being used
    c) Quote the locator definition verbatim with its search scope
    d) List any helper methods or page object properties involved

    Example REQUIRED format in investigation_notes:

    "The stack trace shows failure at DictionaryExplorer.cs:107 in CreateUDETWRLD().
    The retrieved code shows:

    ```csharp
    var contextPane = _contextPane;  // line 107
    ```

    The _contextPane property definition (from retrieved code):
    ```csharp
    _contextPane => _lexicon.GetMainWindow()
        .FindElement(By.AutomationId("AVEVA.DictionaryExplorer.ContextMenu"),
                     searchScope: Scope.Children,
                     timeout: 60)
    ```

    This shows the search targets MainWindow with Scope.Children and a 60-second timeout."

    **Rule 3: Quote Locator Definitions Verbatim**
    If a property or locator is retrieved, ALWAYS quote its full definition in your analysis:
    - Show the search root (e.g., GetMainWindow(), Desktop, parent element)
    - Show the search scope (e.g., Scope.Children, Scope.Descendants)
    - Show the locator pattern (e.g., By.AutomationId, FindFirstDescendant)
    - Show any timeout or retry logic

    **Rule 4: RAG Priority — Source Code Analysis Order**

    When analyzing retrieved source code, follow this STRICT priority:

    1. **HIGHEST PRIORITY:** Crash-site method (where exception was actually thrown)
       - This is production/page-object code, NOT the test method
       - Always analyze and quote this FIRST

    2. **SECOND PRIORITY:** Locator/property definitions used by the crash site
       - Show the full definition with search scope and timeout

    3. **THIRD PRIORITY:** Helper methods called by the crash site

    4. **LOWEST PRIORITY:** Test method
       - ONLY analyze the test method if NO production/page-object code is available
       - NEVER quote the test method when crash-site code exists

    The test method is the LOWEST priority. Only mention it if the failure is directly in test logic.

    **Rule 5: Retrieved Code Shows Intent, Not Reality**
    - ✅ "The code searches under MainWindow with Scope.Children"
    - ❌ "The element is definitely under Desktop" (you don't know the runtime tree)
    - ✅ "The code shows the locator _contextPane searches under MainWindow.Children"
    - ❌ "The locator is incorrect"

    **Rule 6: NEVER Present Inferences as Facts**

    Every inference must be backed by evidence and clearly labeled as inference:

    ❌ BAD: "The application was unresponsive."
    ✅ GOOD: "The automation logs show a 60.5-second gap between actions. This may indicate 
              application slowness, but the logs alone cannot confirm unresponsiveness."

    ❌ BAD: "The locator is wrong because the element is under Desktop."
    ✅ GOOD: "The retrieved code searches under MainWindow.Children. If the context menu is 
              rendered under Desktop (common WPF pattern), this locator would not find it."

    **Rule 6: Separate Evidence from Inference**
    - Start with what you OBSERVE in TRX/logs/source (facts)
    - Then present what MIGHT explain it (inference)
    - Never present framework knowledge as observed evidence
    - Use tentative language: "One possible explanation...", "This suggests...", "This may indicate...", "Verify whether..."

    ===============================================================================
    ## YOUR TASK
    ===============================================================================

    Analyze the failure above. Return ONLY this JSON structure (no markdown, no extra text):

    {
      "investigation_notes": "<Max 400 words. MANDATORY structure:

      1. TRX Evidence (facts only - copy exactly, do NOT rewrite):
         - Exception type and message
         - Stack trace location (file:line)

      2. Log Evidence (facts only - quote exact values):
         - Key timestamps and durations
         - Actions logged
         - Timeout values observed

      3. Retrieved Code Analysis (observations only):
         - Quote the exact failing statement
         - Quote the FULL locator/property definition (search root, scope, AutomationId, timeout)
         - List helper methods/properties involved
         - State what the code ATTEMPTS (NOT what happened at runtime)

      4. Possible Explanation (inference backed by evidence):
         - Reference specific evidence from sections 1-3
         - Use 'This suggests...', 'One possible explanation...', 'This may indicate...'
         - NEVER state inferences as facts
         - Example: 'The logs show a 60s gap, which may indicate slowness' NOT 'The application was slow'>",
      "category": "<locator|timing|app_crash|assertion|environment|data|other>",
      "category_confidence": <0-100>,
      "severity": "<critical|high|medium|low>",
      "severity_confidence": <0-100>,
      "error_summary": "<Copy exception type and message EXACTLY from TRX - do NOT rewrite or add interpretation>",
      "contributing_factors": [
        "<Factual observation from evidence>",
        "<Second observation if applicable>"
      ],
      "hypotheses": [
        {
          "explanation": "<What might have happened - be specific and reference the retrieved code>",
          "issue_owner": "<script|application|insufficient_evidence|uncertain>",
          "confidence": <0-100>,
          "evidence": "<MUST quote: 1) the failing statement from retrieved code, 2) the locator definition used, 3) relevant log lines>",
          "required_to_confirm": "<What information would prove or disprove this hypothesis>"
        }
      ],
      "primary_hypothesis": <index of most likely hypothesis (0-based)>,
      "overall_confidence": "<low|medium|high>",
      "recommended_investigation": [
        "<Concrete step to gather more evidence>",
        "<Second step if applicable>"
      ]
    }

    category options:
    - locator: Element identifier wrong or doesn't exist
    - timing: Element exists but wasn't ready
    - app_crash: Application threw exception or entered invalid state
    - assertion: Test assertion failed (expected != actual)
    - environment: Required file/database/connection/permission missing
    - data: Test data missing or incorrect
    - other: Doesn't fit above

    hypotheses (IMPORTANT - allows multiple possible explanations):
    When evidence is insufficient to determine a single root cause, provide multiple
    hypotheses ranked by confidence. Each hypothesis should:
    - Explain what MIGHT have happened (be specific, not vague)
    - State who would own it using EVIDENCE-BASED RULES:
      • "script" = ONLY with direct proof (e.g., stack trace in test file, source code shows invalid locator)
      • "application" = ONLY with direct proof (e.g., app crash log, ProcessExitedException)
      • "insufficient_evidence" = Common UI automation exception (ElementNotAvailableException, 
        TimeoutException, WaitTimeoutException, etc.) WITHOUT supporting evidence
      • "uncertain" = Conflicting/ambiguous evidence
    - Give confidence percentage (0-100)
    - Quote the OBSERVED evidence from TRX/logs/source (never runtime claims from code alone)
    - Clearly separate observed facts from inferred behavior
    - NEVER claim "the application froze" or "element is under Desktop" without direct log/TRX evidence
    - Use wording like: "The retrieved code shows...", "One possible explanation...", "This would need verification"
    - State what information would confirm or reject this hypothesis

    ⚠️ CRITICAL CLASSIFICATION RULES:
    For ElementNotAvailableException, ElementNotEnabledException, TimeoutException, WaitTimeoutException:
    - DO NOT automatically classify as "script" or "application"
    - Use "insufficient_evidence" unless you have DIRECT PROOF from:
      ✅ Stack trace showing test code error
      ✅ Retrieved source showing invalid locator
      ✅ Application crash logs
      ✅ ProcessExitedException in TRX
      ✅ Screenshot showing app state
      ✅ UI Automation tree dump
    - Provide BOTH script and application hypotheses when using "insufficient_evidence"

    Example for TimeoutException with no additional evidence:
    {
      "explanation": "Based on available artifacts, cannot determine if timeout is due to incorrect wait logic (script) or slow UI rendering (application)",
      "issue_owner": "insufficient_evidence",
      "confidence": 20,
      "evidence": "TRX shows WaitTimeoutException after 60s. No app crash logs. No stack trace in test logic. Retrieved code shows standard FindFirstDescendant pattern.",
      "required_to_confirm": "Application performance logs, UI render traces, or FlaUInspect verification of element state"
    }

    Guidelines for hypotheses:
    - If confidence in top hypothesis is below 70%, provide at least 2 hypotheses
    - If multiple hypotheses have similar confidence (within 15%), include all
    - If one hypothesis has 80%+ confidence, you may provide only that one
    - Total confidence across all hypotheses does NOT need to sum to 100%
    - Each hypothesis should be distinct and testable

    overall_confidence:
    - "high"   : Top hypothesis has 80%+ confidence
    - "medium" : Top hypothesis has 60-79% confidence  
    - "low"    : Top hypothesis has <60% confidence OR multiple competing hypotheses

    recommended_investigation:
    - ALWAYS start with verification/investigation steps (use inspection tools, compare logs, etc.)
    - Help determine which hypothesis is correct BEFORE changing code
    - Be specific (e.g., "Use FlaUInspect to verify the context menu's parent element")
    - The FINAL step may suggest a code change, but ONLY if phrased as: "If investigation confirms [hypothesis], then [code change]"
    - Never recommend code changes without verification first

    confidence_scores (0-100):
    - 90-100%: Very confident, clear evidence
    - 70-89%: Reasonably confident, good evidence
    - 50-69%: Moderate confidence, some ambiguity
    - Below 50%: Low confidence, limited/conflicting evidence

    Return ONLY valid JSON. No extra text.
    """;
    }

    /// <summary>
    /// Format screenshot/vision analysis evidence for the LLM prompt
    /// </summary>
    private static string FormatScreenshotEvidence(List<ScreenshotAnalysis>? screenshots)
    {
        if (screenshots == null || !screenshots.Any())
        {
            return "(No screenshots captured at failure time)";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**{screenshots.Count} screenshot(s) captured at failure:**\n");

        foreach (var screenshot in screenshots)
        {
            sb.AppendLine($"Screenshot: {System.IO.Path.GetFileName(screenshot.ScreenshotPath)}");
            sb.AppendLine($"Confidence: {screenshot.ConfidenceScore}%");
            sb.AppendLine($"\nDescription: {screenshot.Description}");

            if (!string.IsNullOrEmpty(screenshot.RelevanceToFailure))
            {
                sb.AppendLine($"\nRelevance to Failure: {screenshot.RelevanceToFailure}");
            }

            if (screenshot.ObservedElements.Any())
            {
                sb.AppendLine($"\nObserved UI Elements:");
                foreach (var element in screenshot.ObservedElements.Take(10))
                {
                    sb.AppendLine($"  • {element}");
                }
            }

            if (screenshot.ErrorsVisible.Any())
            {
                sb.AppendLine($"\n**CRITICAL - Error Dialogs Visible:**");
                foreach (var error in screenshot.ErrorsVisible)
                {
                    sb.AppendLine($"  • \"{error}\"");
                }
            }

            if (screenshot.CategoriesVisible.Any())
            {
                sb.AppendLine($"\nCategories detected in UI: {string.Join(", ", screenshot.CategoriesVisible)}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build prompt for Call A with separated application vs test evidence for better classification.
    /// </summary>
    public static string BuildInvestigationPromptWithSeparatedEvidence(
        TestResult failure,
        SeparatedEvidence evidence,
        string environment,
        string? ragContext,
        List<ScreenshotAnalysis>? screenshots = null)
    {
        var errorMsg = TruncateWithMarker(failure.ErrorMessage, 800);
        var stackTrace = TruncateWithMarker(failure.StackTrace, 2000);
        var ragContextFormatted = FormatRagContext(ragContext, 4000);

        // Format screenshot evidence if available
        var screenshotEvidence = FormatScreenshotEvidence(screenshots);

        return $$"""
    # {{failure.TestName}} — Investigation Phase

    ===============================================================================
    ## TEST-SIDE EVIDENCE (what the test framework captured)
    ===============================================================================

    **Automation Framework:** FlaUI (Windows UI Automation for WPF apps)
    **Test Framework:** MSTest
    **Application Under Test:** AVEVA Dabacon (WPF desktop application)
    **Test:** {{failure.TestName}}
    **Environment:** {{environment}}

    **Test Exception:**
    {{errorMsg}}

    **Test Stack Trace:**
    {{stackTrace}}

    **Test Execution Logs:**
    {{(evidence.TestEvidence.Length > 0 ? evidence.TestEvidence : "(none captured)")}}

    **Test Source Code (from stack trace):**
    {{(ragContextFormatted != "(not provided)" ? ragContextFormatted : "(none retrieved)")}}

    ===============================================================================
    ## APPLICATION-SIDE EVIDENCE (what the application logged/exhibited)
    ===============================================================================

    {{evidence.ApplicationEvidence}}

    ===============================================================================
    ## SCREENSHOT/UI EVIDENCE (what was visible on screen at failure)
    ===============================================================================

    {{screenshotEvidence}}

    ===============================================================================
    ## CLASSIFICATION GUIDANCE
    ===============================================================================

    **How to determine issue ownership:**

    1. **If SCREENSHOT shows error dialogs with quoted text:**
       → PRIORITIZE the screenshot evidence - it's the most direct proof of what failed
       → Quote the exact dialog text in your evidence field
       → Confidence should be HIGH (85-98%) when you have exact quoted dialog strings

    2. **If APPLICATION-SIDE EVIDENCE shows errors/crashes:**
       → Create a hypothesis with "issue_owner": "application" (high confidence)
       → Evidence: Dabacon errors, access denied, app crashes, performance gaps

    3. **If APPLICATION-SIDE EVIDENCE is empty/clean:**
       → Create a hypothesis with "issue_owner": "script" (high confidence)
       → Evidence: Locator issues, timing problems, assertion logic errors

    4. **If BOTH sides have evidence:**
       → Create multiple hypotheses for both script and application possibilities
       → Example: App slow (application) + insufficient timeout (script)

    5. **If evidence is insufficient:**
       → Mark "overall_confidence": "low"
       → State what evidence is missing in "required_to_confirm"

    **Important Rules:**

    - Screenshot dialog text is PRIMARY evidence - quote it directly
    - "files are not available, check permissions" indicates ENVIRONMENT issue (storage/network)
    - Dabacon errors ALWAYS indicate application issues (database engine problems)
    - "Access denied" or "E_ACCESSDENIED" indicates environment/application permission issues
    - Long time gaps (>10s) + MainWindow unavailable suggests application hang/freeze
    - ElementNotFoundException could be script (wrong locator) OR application (element not rendered)
    - Timeouts could be script (insufficient wait) OR application (performance issue)

    ===============================================================================
    ## ANALYSIS RULES
    ===============================================================================

    **Rule 1: Evidence-Only Claims**
    Quote the specific log line, error message, screenshot dialog text, or source code that proves every claim.
    If evidence is insufficient, say "uncertain" and state what is missing.

    **Rule 2: Separate Script vs Application vs Environment Issues**
    - Environment issues: Storage unavailable, network paths, permissions, external service failures
    - Script issues: Wrong AutomationId, insufficient timeout, bad assertion, test logic errors
    - Application issues: Dabacon errors, crashes, access denied, performance degradation, rendering bugs
    - Use the separated evidence sections above to distinguish between them.

    **Rule 3: Conciseness Required**
    Your investigation_notes MUST NOT exceed ~300 words. Be direct and factual.

    **Rule 4: Separate Evidence from Inference**
    - Start with what you OBSERVE in TRX/logs/screenshots/source (facts)
    - Then present what MIGHT explain it (inference)
    - Never present framework knowledge as observed evidence
    - Use tentative language: "One possible explanation...", "This suggests...", "Verify whether..."

    **Rule 5: Don't Recommend Finding Evidence You Already Have**
    - If screenshot shows dialog text, don't ask the user to examine screenshots manually - you already have the extracted dialog text
    - Recommend the next UNRESOLVED step (e.g., "verify network path accessibility")

    ===============================================================================
    ## FAULT ATTRIBUTION DECISION PROCEDURE
    ===============================================================================

    Follow this procedure IN ORDER to classify primary fault:

    1. **Screenshot Evidence Check (HIGHEST PRIORITY)**:
       - If screenshot shows an **error dialog** with text like "Failed to open MDB", "Access denied", "Network path not found", etc.
         → Primary: ENVIRONMENT (high confidence 85-95%)
         → Check if script could have detected/handled this → Secondary: SCRIPT if missing guard logic

       - If screenshot shows **application crash/freeze** (blank screen, hung UI, process terminated)
         → Primary: APPLICATION (high confidence 85-95%)
         → Check if test triggered via invalid input → Secondary: SCRIPT if bad test data

    2. **Stack Trace Origin Check (if no screenshot or screenshot is ambiguous)**:
       - If innermost frame is in **test code** (test project files, page objects, helper classes)
         → Primary: SCRIPT (confidence 70-85%)

       - If innermost frame is in **application code** (NOT FlaUI, NOT test project)
         → Primary: APPLICATION (confidence 70-85%)

       - If innermost frame is in **FlaUI/automation framework**
         → Indeterminate — proceed to step 3

    3. **Log Evidence Check (if steps 1-2 inconclusive)**:
       - Application logs show errors/exceptions BEFORE test exception
         → Primary: APPLICATION (confidence 60-75%)

       - Automation logs show test took specific action that immediately caused failure
         → Primary: SCRIPT (confidence 60-75%)

       - Logs show external resource unavailable (network, DB, file system)
         → Primary: ENVIRONMENT (confidence 70-85%)

    4. **Retrieved Code Analysis (lowest priority, use only when all above are inconclusive)**:
       - Code shows clear defect (null dereference, wrong locator value, missing timeout)
         → Primary: SCRIPT (confidence 50-65%)

       - Code looks correct but failure still occurred
         → Indeterminate or ENVIRONMENT (confidence 40-55%)

    SECONDARY CONTRIBUTING FACTORS:
    - If primary is ENVIRONMENT but script lacks defensive checks (null guards, dialog detection)
      → Add secondary SCRIPT factor with description of missing guard logic

    - If primary is APPLICATION but test used invalid/boundary data that triggered app bug
      → Add secondary SCRIPT factor explaining data issue

    - If primary is SCRIPT but application was also slow/unresponsive during failure window
      → Add secondary ENVIRONMENT or APPLICATION factor

    ALWAYS include secondary factors when evidence suggests compounding causes.
    Do NOT force a single-cause attribution when multiple factors contributed.

    SUGGESTED FIX GATING (CRITICAL — follow these rules exactly):

    ONLY propose a code fix (non-null suggested_fix) when ALL conditions are met:
    1. Crash site was matched via **exact symbol lookup** (retrieval category will say "Exact Symbol Match" or similar)
    2. Primary fault attribution is **SCRIPT** OR SCRIPT is listed as a secondary factor
    3. Retrieved source code shows the actual failing logic (not just similar code from a different file)

    DO NOT propose a code fix when:
    ❌ Retrieval used semantic/embedding fallback (may have retrieved wrong file)
    ❌ Primary fault is ENVIRONMENT or APPLICATION with no SCRIPT secondary factor
    ❌ Retrieved code doesn't match the stack trace file:line exactly
    ❌ The fix would require knowledge of helper types/methods not visible in retrieved context

    When proposing a fix:
    - Use ONLY variable names, types, and patterns visible in the retrieved source
    - Include a disclaimer in explanation: "This is a draft fix based on retrieved context. Verify actual helper/type signatures before applying."
    - Set confidence_level:
      • "high"   : Exact match, simple change (add timeout, fix obvious typo)
      • "medium" : Exact match, but change requires assumptions about helper methods
      • "low"    : Semantic match OR complex refactor needed

    When NOT proposing a fix:
    - Set all fields to null
    - In "explanation", state the reason: 
      • "No code fix proposed — root cause is environmental (missing MDB file)"
      • "No code fix proposed — crash site retrieved via semantic match (risk of wrong file)"
      • "No code fix proposed — fault is in application code, not test script"
    - Set gating_reason appropriately

    ===============================================================================
    ## YOUR TASK
    ===============================================================================

    Analyze the failure above using the separated evidence. Return ONLY this JSON structure (no markdown, no extra text):

    {
      "investigation_notes": "<Max 300 words: what failed, where (file:line), what TEST-SIDE shows, what APPLICATION-SIDE shows, what SCREENSHOT shows, why it failed. Quote evidence from all sources.>",
      "category": "<locator|timing|app_crash|assertion|environment|data|other>",
      "category_confidence": <0-100>,
      "severity": "<critical|high|medium|low>",
      "severity_confidence": <0-100>,
      "error_summary": "<One sentence: what failed, where, with key details like timeout, AutomationId, error code>",
      "fault_attribution": {
        "primary": "<SCRIPT|APPLICATION|ENVIRONMENT|DATA|INDETERMINATE>",
        "confidence": <0-100>,
        "secondary_contributing_factors": [
          {
            "type": "<SCRIPT|APPLICATION|ENVIRONMENT|DATA>",
            "description": "<Short description of secondary factor>",
            "why_it_matters": "<What happens if only the primary cause is fixed>"
          }
        ]
      },
      "contributing_factors": [
        "<Factual observation from evidence>",
        "<Second observation if applicable>"
      ],
      "suggested_fix": {
        "file_path": "<Relative path to file, or null if no code fix applicable>",
        "current_code": "<Exact problematic snippet from retrieved source, or null>",
        "proposed_code": "<Corrected snippet, or null>",
        "explanation": "<Why this addresses the root cause, tied to evidence, or reason why no fix is proposed (e.g., 'No code fix proposed — root cause is environmental')>",
        "confidence_level": "<high|medium|low>",
        "gating_reason": "<'Exact crash site confirmed via symbol match' OR 'Semantic match only, avoid fixing wrong file' OR 'Root cause is environmental/data, not a code defect'>"
      },
      "hypotheses": [
        {
          "explanation": "<What might have happened - be specific>",
          "issue_owner": "<script|application|uncertain>",
          "confidence": <0-100>,
          "evidence": "<Quote from TEST-SIDE or APPLICATION-SIDE evidence supporting this>",
          "required_to_confirm": "<What information would prove or disprove this hypothesis>"
        }
      ],
      "primary_hypothesis": <index of most likely hypothesis (0-based)>,
      "overall_confidence": "<low|medium|high>",
      "recommended_investigation": [
        "<Concrete step to gather more evidence>",
        "<Second step if applicable>"
      ]
    }

    category options:
    - locator: Element identifier wrong or doesn't exist
    - timing: Element exists but wasn't ready
    - app_crash: Application threw exception or entered invalid state
    - assertion: Test assertion failed (expected != actual)
    - environment: Required file/database/connection/permission missing
    - data: Test data missing or incorrect
    - other: Doesn't fit above

    hypotheses (IMPORTANT - use APPLICATION-SIDE evidence for application hypotheses):
    - If APPLICATION-SIDE has errors → create "application" hypothesis with evidence
    - If APPLICATION-SIDE is clean → create "script" hypothesis
    - If both have evidence → create hypotheses for both possibilities
    - Each hypothesis confidence should reflect how strongly the evidence supports it
    - Quote OBSERVED evidence (from TRX/logs/source), not framework assumptions
    - Clearly separate observed facts from inferred behavior
    - NEVER present framework patterns or general knowledge as observed evidence

    Guidelines:
    - Top hypothesis confidence <70% → provide at least 2 hypotheses
    - Multiple hypotheses with similar confidence (within 15%) → include all
    - One hypothesis 80%+ confidence → may provide only that one
    - Each hypothesis must be distinct and testable

    overall_confidence:
    - "high"   : Top hypothesis has 80%+ confidence, clear evidence from one side
    - "medium" : Top hypothesis has 60-79% confidence, some ambiguity
    - "low"    : Top hypothesis has <60% confidence OR conflicting evidence from both sides

    recommended_investigation:
    - ALWAYS start with verification/investigation steps (use inspection tools, compare logs, etc.)
    - Help determine which hypothesis is correct BEFORE changing code
    - Be specific (e.g., "Use FlaUInspect to verify the context menu's parent element")
    - The FINAL step may suggest a code change, but ONLY if phrased as: "If investigation confirms [hypothesis], then [code change]"
    - Never recommend code changes without verification first

    Return ONLY valid JSON. No extra text.
    """;
    }

    /// <summary>
    /// Build prompt for Call B: Fix/suggestion phase.
    /// Uses investigation context from Call A to provide targeted fixes.
    /// </summary>
    public static string BuildFixPrompt(
        TestResult failure,
        string investigationSummary,
        string? ragContext)
    {
        var ragContextFormatted = FormatRagContext(ragContext, 4000);

        return $$"""
    # {{failure.TestName}} — Fix Suggestions Phase

    ===============================================================================
    ## INVESTIGATION SUMMARY (from Call A)
    ===============================================================================

    {{investigationSummary}}

    ===============================================================================
    ## SOURCE CODE CONTEXT
    ===============================================================================

    {{(ragContextFormatted != "(not provided)" ? ragContextFormatted : "(none retrieved)")}}

    ===============================================================================
    ## YOUR TASK
    ===============================================================================

    Based on the investigation above, provide concrete fix suggestions and code.

    **IMPORTANT — CODE FIXES:**
    When source code is provided and the issue is fixable (locator, timing, assertion, test logic),
    you MUST provide a concrete code fix in "code_snippet". Only return null if:
    - No source code was provided, OR
    - The issue is purely environmental/infrastructure (missing file, network, permissions), OR
    - The issue is an application bug that cannot be fixed in test code

    When providing a fix:
    - Use the actual patterns and variable names from the retrieved source code
    - Include enough context (method signature + corrected logic)
    - Show specific line-level changes (e.g., increase timeout, fix AutomationId, add null check)
    - Use FlaUI patterns already present in the codebase (NOT Selenium/WebDriver)

    Return ONLY this JSON structure (no markdown, no extra text):

    {
      "suggestions": [
        {
          "action": "<Specific fix with exact file:line reference based on source code>",
          "type": "<locator|wait|code|environment|data|infrastructure|logic|investigation>",
          "priority": "<immediate|soon|later>",
          "applies_to_hypothesis": <index or null>
        }
      ],
      "code_snippet": "<Corrected code showing the fixed method or section. Use actual patterns from retrieved source. Return null if no source provided or issue is environmental/infrastructure.>"
    }

    suggestion type options:
    - locator: Fix element identifier/selector
    - wait: Add or adjust timeout/wait condition
    - code: Fix test logic, assertion, or flow
    - environment: Fix configuration, file path, or setup
    - data: Fix test data or input
    - infrastructure: Fix service, database, or external dependency
    - logic: Fix application code (if issue_owner=application)
    - investigation: Steps to gather more evidence (for uncertain cases)

    priority options:
    - immediate: Blocks testing, must fix now
    - soon: Causes frequent failures, fix in next sprint
    - later: Low impact, can defer

    applies_to_hypothesis:
    - If investigation identified multiple hypotheses, specify which hypothesis index (0-based) this suggestion applies to
    - Use null if suggestion applies regardless of hypothesis

    Return ONLY valid JSON. No extra text.
    """;
    }
}
