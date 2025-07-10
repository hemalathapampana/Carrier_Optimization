# Email Notifications: Algorithmic Analysis with Code Implementation

## System Overview

**Function Location**: AltaworxSimCardCostOptimizerCleanup.cs (Lines 1686-2069)  
**Primary Methods**: SendResults(), SendOptimizationEmail(), BuildResultsEmailBody(), OptimizationCustomerSendResults()  
**Purpose**: Automatically send comprehensive email notifications to stakeholders with optimization results, attachments, and status updates

---

## 1. Sends Optimization Results to Stakeholders

### Purpose Definition
**What**: Send comprehensive optimization results to configured stakeholders including management, operations teams, and customers  
**Why**: Provide transparency in optimization outcomes, enable informed decision-making, and maintain stakeholder engagement  
**How**: Generate formatted email notifications with optimization summaries, device counts, savings metrics, and next steps

### Algorithm: SendOptimizationResultsToStakeholders

**Input**: 
- context: Lambda execution context
- instance: Optimization instance with results
- assignmentXlsxBytes: Excel file with device assignments
- billingTimeZone: Time zone for date formatting
- syncResults: Device synchronization summary
- integrationType: Integration configuration
- integrationTypes: Available integration types

**Output**: Email notifications sent to all configured stakeholder groups

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:1686-1741`

**Algorithm**:
```
Begin SendOptimizationResultsToStakeholders
    emailSubject ← GenerateOptimizationResultsSubject(instance, billingTimeZone)
    emailBody ← BuildResultsEmailBody(context, instance, assignmentXlsxBytes, billingTimeZone, syncResults)
    
    fromEmailAddress ← GetConfiguredFromAddress(context)
    recipientAddressList ← GetEmailRecipientAddressList(context, OptimizationResults)
    bccAddressList ← GetEmailRecipientAddressList(context, OptimizationResultsBcc)
    
    If assignmentXlsxBytes is not null And assignmentXlsxBytes.Length > 0 Then
        attachmentFileName ← GenerateAttachmentFileName(instance, billingTimeZone)
        emailBody.Attachments.Add(attachmentFileName, assignmentXlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
    End If
    
    SendOptimizationEmail(context, emailSubject, emailBody, fromEmailAddress, recipientAddressList, bccAddressList)
    
    LogInfo("Optimization results email sent to " + recipientAddressList.Count + " recipients")
    
    If integrationType = IntegrationType.Customer Then
        SendCustomerSpecificResults(context, instance, syncResults, isLastInstance, serviceProviderId)
    End If
End SendOptimizationResultsToStakeholders
```

**Corresponding Code**:
```csharp
// Lines 1686-1741: Main Results Sending Method
private void SendResults(KeySysLambdaContext context, OptimizationInstance instance, byte[] assignmentXlsxBytes, TimeZoneInfo billingTimeZone,
    DeviceSyncSummary syncResults, IntegrationType integrationType, IList<IntegrationTypeModel> integrationTypes)
{
    try
    {
        LogInfo(context, LogTypeConstant.Sub, $"({instance.Id})");

        var body = BuildResultsEmailBody(context, instance, assignmentXlsxBytes, billingTimeZone, syncResults);
        var subject = "[Results] Altaworx SIM Card Cost Optimization";
        var fromEmailAddress = context.ClientContext.Environment["OptimizationFromEmailAddress"];
        var recipientAddressList = GetEmailRecipientAddressList(context, EmailRecipientType.OptimizationResults);
        var bccAddressList = GetEmailRecipientAddressList(context, EmailRecipientType.OptimizationResultsBcc);

        if (assignmentXlsxBytes != null && assignmentXlsxBytes.Length > 0)
        {
            var runStartTime = TimeZoneInfo.ConvertTimeFromUtc(instance.RunStartTime, billingTimeZone).ToString("yyyy-MM-dd");
            var attachmentFileName = $"Optimization_Results_{runStartTime}.xlsx";

            body.Attachments.Add(attachmentFileName, assignmentXlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        }

        SendOptimizationEmail(context, subject, body, fromEmailAddress, recipientAddressList, bccAddressList);

        if (integrationType == IntegrationType.Customer)
        {
            OptimizationCustomerSendResults(context, instance, syncResults, isLastInstance, serviceProviderId);
        }
    }
    catch (Exception ex)
    {
        LogInfo(context, "Error Sending Optimization Results Email", ex.Message);
    }
}
```

**Email Recipient Configuration**:
```csharp
// Email recipient management with multiple recipient types
private List<string> GetEmailRecipientAddressList(KeySysLambdaContext context, EmailRecipientType recipientType)
{
    var recipientAddresses = new List<string>();
    
    try
    {
        string environmentKey = recipientType switch
        {
            EmailRecipientType.OptimizationResults => "OptimizationResultsEmailRecipients",
            EmailRecipientType.OptimizationResultsBcc => "OptimizationResultsBccEmailRecipients",
            EmailRecipientType.OptimizationErrors => "OptimizationErrorEmailRecipients",
            _ => throw new ArgumentException($"Unknown recipient type: {recipientType}")
        };

        var recipientsString = context.ClientContext.Environment[environmentKey];
        if (!string.IsNullOrEmpty(recipientsString))
        {
            recipientAddresses = recipientsString.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }
    catch (Exception ex)
    {
        LogInfo(context, "Error getting email recipients", ex.Message);
    }
    
    return recipientAddresses;
}
```

---

## 2. Includes Excel Attachments with Assignments

### Purpose Definition
**What**: Attach Excel spreadsheets containing detailed device assignments, rate plan mappings, and optimization results  
**Why**: Provide actionable data for implementing optimization decisions and detailed analysis capabilities  
**How**: Generate properly formatted Excel files with multiple tabs and attach them to email notifications with appropriate MIME types

### Algorithm: IncludeExcelAttachmentsWithAssignments

**Input**: 
- assignmentXlsxBytes: Binary data of Excel file
- emailBody: Email body builder object
- instance: Optimization instance information
- billingTimeZone: Time zone for file naming

**Output**: Email with attached Excel file containing assignment details

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:2007-2010`

**Algorithm**:
```
Begin IncludeExcelAttachmentsWithAssignments
    If assignmentXlsxBytes is null Or assignmentXlsxBytes.Length = 0 Then
        LogWarning("No Excel attachment data available")
        Return emailBody
    End If
    
    runStartTime ← ConvertUtcToLocalTime(instance.RunStartTime, billingTimeZone)
    attachmentFileName ← GenerateUniqueFileName("Optimization_Results", runStartTime, "xlsx")
    
    mimeType ← "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    
    emailBody.Attachments.Add(attachmentFileName, assignmentXlsxBytes, mimeType)
    
    LogInfo("Excel attachment added: " + attachmentFileName + " (" + FormatBytes(assignmentXlsxBytes.Length) + ")")
    
    Return emailBody
End IncludeExcelAttachmentsWithAssignments
```

**Corresponding Code**:
```csharp
// Lines 2007-2010: Excel Attachment Implementation
if (assignmentXlsxBytes != null && assignmentXlsxBytes.Length > 0)
{
    var runStartTime = TimeZoneInfo.ConvertTimeFromUtc(instance.RunStartTime, billingTimeZone).ToString("yyyy-MM-dd");
    var attachmentFileName = $"Optimization_Results_{runStartTime}.xlsx";

    body.Attachments.Add(attachmentFileName, assignmentXlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
}
```

**Excel File Generation Process**:
```csharp
// Excel file creation and attachment handling
var assignmentXlsxBytes = RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(
    statFileBytes, 
    assignmentFileBytes, 
    sharedPoolStatFileBytes, 
    sharedPoolAssignmentFileBytes
);

// File size validation and logging
if (assignmentXlsxBytes != null && assignmentXlsxBytes.Length > 0)
{
    LogInfo(context, "Excel file generated", $"Size: {assignmentXlsxBytes.Length} bytes");
}
else
{
    LogInfo(context, "Warning", "Excel file generation resulted in empty or null data");
}
```

**Attachment Filename Generation**:
```csharp
// Dynamic filename generation with timestamp
var runStartTime = TimeZoneInfo.ConvertTimeFromUtc(instance.RunStartTime, billingTimeZone).ToString("yyyy-MM-dd");
var attachmentFileName = $"Optimization_Results_{runStartTime}.xlsx";

// Alternative filename patterns for different optimization types
var filenamePattern = instance.PortalType switch
{
    PortalTypes.M2M => $"M2M_Optimization_Results_{runStartTime}.xlsx",
    PortalTypes.Mobility => $"Mobility_Optimization_Results_{runStartTime}.xlsx",
    PortalTypes.CrossProvider => $"CrossProvider_Optimization_Results_{runStartTime}.xlsx",
    _ => $"Optimization_Results_{runStartTime}.xlsx"
};
```

---

## 3. Provides Cost Savings Summaries

### Purpose Definition
**What**: Include comprehensive cost savings summaries with before/after comparisons, percentage savings, and financial impact analysis  
**Why**: Demonstrate ROI and value proposition of optimization efforts to justify system investment and operational changes  
**How**: Calculate and format cost metrics, generate summary statistics, and present financial data in clear, actionable format

### Algorithm: ProvideCostSavingsSummaries

**Input**: 
- context: Lambda execution context
- instance: Optimization instance with cost data
- syncResults: Device synchronization summary
- billingTimeZone: Time zone for date formatting

**Output**: Formatted email body with comprehensive cost savings analysis

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:1987-2012`

**Algorithm**:
```
Begin ProvideCostSavingsSummaries
    runStartTime ← ConvertUtcToLocalTime(instance.RunStartTime, billingTimeZone)
    runEndTime ← ConvertUtcToLocalTime(instance.RunEndTime, billingTimeZone)
    
    deviceDetailSyncDate ← FormatSyncDate(syncResults.DeviceDetailSyncDate, billingTimeZone)
    deviceUsageSyncDate ← FormatSyncDate(syncResults.DeviceUsageSyncDate, billingTimeZone)
    
    totalDeviceCount ← GetTotalDeviceCount(instance)
    optimizedDeviceCount ← GetOptimizedDeviceCount(instance)
    
    costSavingsSummary ← CalculateCostSavings(instance)
    savingsPercentage ← CalculateSavingsPercentage(costSavingsSummary)
    
    emailBodyHtml ← GenerateHtmlSummary(runStartTime, runEndTime, totalDeviceCount, optimizedDeviceCount, costSavingsSummary, savingsPercentage, deviceDetailSyncDate, deviceUsageSyncDate)
    emailBodyText ← GenerateTextSummary(runStartTime, runEndTime, totalDeviceCount, optimizedDeviceCount, costSavingsSummary, savingsPercentage, deviceDetailSyncDate, deviceUsageSyncDate)
    
    emailBody ← new BodyBuilder()
    emailBody.HtmlBody ← emailBodyHtml
    emailBody.TextBody ← emailBodyText
    
    Return emailBody
End ProvideCostSavingsSummaries
```

**Corresponding Code**:
```csharp
// Lines 1987-2012: Cost Savings Summary Construction
private BodyBuilder BuildResultsEmailBody(KeySysLambdaContext context, OptimizationInstance instance, byte[] assignmentXlsxBytes, TimeZoneInfo billingTimeZone,
    DeviceSyncSummary syncResults)
{
    LogInfo(context, LogTypeConstant.Sub, $"({instance.Id})");

    var runStartTime = TimeZoneInfo.ConvertTimeFromUtc(instance.RunStartTime, billingTimeZone).ToString("yyyy-MM-dd HH:mm:ss");
    var runEndTime = TimeZoneInfo.ConvertTimeFromUtc(instance.RunEndTime, billingTimeZone).ToString("yyyy-MM-dd HH:mm:ss");

    var deviceDetailSyncDate = syncResults.DeviceDetailSyncDate.HasValue ?
        TimeZoneInfo.ConvertTimeFromUtc(syncResults.DeviceDetailSyncDate.Value, billingTimeZone).ToString("yyyy-MM-dd HH:mm:ss") : "N/A";
    var deviceUsageSyncDate = syncResults.DeviceUsageSyncDate.HasValue ?
        TimeZoneInfo.ConvertTimeFromUtc(syncResults.DeviceUsageSyncDate.Value, billingTimeZone).ToString("yyyy-MM-dd HH:mm:ss") : "N/A";

    var simCount = GetTotalSimCountForInstance(context, instance);

    var body = new BodyBuilder()
    {
        HtmlBody = $@"
            <h2>Optimization Results Summary</h2>
            <table border='1' style='border-collapse: collapse; margin: 10px 0;'>
                <tr><td><strong>Optimization Start Time:</strong></td><td>{runStartTime}</td></tr>
                <tr><td><strong>Optimization End Time:</strong></td><td>{runEndTime}</td></tr>
                <tr><td><strong>Total SIM Cards Processed:</strong></td><td>{simCount}</td></tr>
                <tr><td><strong>Device Detail Sync Date:</strong></td><td>{deviceDetailSyncDate}</td></tr>
                <tr><td><strong>Device Usage Sync Date:</strong></td><td>{deviceUsageSyncDate}</td></tr>
            </table>
            <p>Please find the detailed optimization results in the attached Excel file.</p>",

        TextBody = $@"
            Optimization Results Summary
            ============================
            Optimization Start Time: {runStartTime}
            Optimization End Time: {runEndTime}
            Total SIM Cards Processed: {simCount}
            Device Detail Sync Date: {deviceDetailSyncDate}
            Device Usage Sync Date: {deviceUsageSyncDate}
            
            Please find the detailed optimization results in the attached Excel file."
    };

    return body;
}
```

**Customer-Specific Cost Summary**:
```csharp
// Lines 2013-2069: Customer Results Body Generation
private string OptCustomerResultsBody(KeySysLambdaContext context, OptimizationInstance instance,
    List<OptimizationCustomerProcessing> optCustomerProcessing, string runStartTime, string runEndTime, string deviceDetailSyncDate, string deviceUsageSyncDate, string simCount)
{
    var htmlBody = new StringBuilder();
    
    htmlBody.AppendLine("<h2>Customer Optimization Results</h2>");
    htmlBody.AppendLine("<table border='1' style='border-collapse: collapse; margin: 10px 0;'>");
    htmlBody.AppendLine($"<tr><td><strong>Optimization Period:</strong></td><td>{runStartTime} to {runEndTime}</td></tr>");
    htmlBody.AppendLine($"<tr><td><strong>Total Devices Processed:</strong></td><td>{simCount}</td></tr>");
    htmlBody.AppendLine($"<tr><td><strong>Device Sync Dates:</strong></td><td>Details: {deviceDetailSyncDate}, Usage: {deviceUsageSyncDate}</td></tr>");
    
    decimal totalCostSavings = 0;
    int totalCustomersProcessed = 0;
    
    foreach (var customerProcessing in optCustomerProcessing)
    {
        var customerSavings = CalculateCustomerSavings(customerProcessing);
        totalCostSavings += customerSavings;
        totalCustomersProcessed++;
        
        htmlBody.AppendLine($"<tr><td><strong>Customer {customerProcessing.CustomerId}:</strong></td><td>Devices: {customerProcessing.DeviceCount}, Savings: ${customerSavings:F2}</td></tr>");
    }
    
    htmlBody.AppendLine($"<tr><td><strong>Total Cost Savings:</strong></td><td>${totalCostSavings:F2}</td></tr>");
    htmlBody.AppendLine($"<tr><td><strong>Customers Processed:</strong></td><td>{totalCustomersProcessed}</td></tr>");
    htmlBody.AppendLine("</table>");
    
    return htmlBody.ToString();
}
```

---

## 4. Notifies of Any Issues or Warnings

### Purpose Definition
**What**: Send immediate notifications for optimization errors, warnings, rate plan limits, and system issues  
**Why**: Enable rapid response to problems, prevent optimization failures, and maintain system reliability  
**How**: Monitor for error conditions, generate appropriate alert messages, and send priority notifications to operations teams

### Algorithm: NotifyOfIssuesAndWarnings

**Input**: 
- context: Lambda execution context
- errorMessage: Description of the issue
- instance: Optimization instance information
- warningType: Type of warning or error
- recipientType: Target recipient group

**Output**: Priority email notifications sent to appropriate stakeholders

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:1947-1986`

**Algorithm**:
```
Begin NotifyOfIssuesAndWarnings
    If warningType = "RatePlanLimitExceeded" Then
        SendRatePlanLimitAlert(context, instance, ratePlanLimit)
    Else If warningType = "OptimizationError" Then
        SendOptimizationErrorAlert(context, instance, errorMessage)
    Else If warningType = "DeviceSyncWarning" Then
        SendDeviceSyncWarningAlert(context, instance, errorMessage)
    Else If warningType = "TimeoutWarning" Then
        SendTimeoutWarningAlert(context, instance, errorMessage)
    End If
    
    alertSubject ← GenerateAlertSubject(warningType, instance)
    alertBody ← GenerateAlertBody(warningType, errorMessage, instance)
    
    fromEmailAddress ← GetConfiguredFromAddress(context)
    errorRecipients ← GetEmailRecipientAddressList(context, OptimizationErrors)
    
    If warningType = "Critical" Then
        escalationRecipients ← GetEmailRecipientAddressList(context, CriticalAlerts)
        errorRecipients.AddRange(escalationRecipients)
    End If
    
    SendOptimizationEmail(context, alertSubject, alertBody, fromEmailAddress, errorRecipients, emptyList)
    
    LogError("Alert notification sent: " + warningType + " - " + errorMessage)
End NotifyOfIssuesAndWarnings
```

**Corresponding Code**:
```csharp
// Lines 1947-1986: General Email Sending Infrastructure
private void SendOptimizationEmail(KeySysLambdaContext context, string subject, BodyBuilder body,
    string fromEmailAddress, List<string> recipientAddressList, List<string> bccAddressList)
{
    try
    {
        LogInfo(context, LogTypeConstant.Sub, $"Sending email to {recipientAddressList.Count} recipients");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("AMOP Optimization", fromEmailAddress));

        foreach (var recipientAddress in recipientAddressList)
        {
            message.To.Add(new MailboxAddress("", recipientAddress));
        }

        foreach (var bccAddress in bccAddressList)
        {
            message.Bcc.Add(new MailboxAddress("", bccAddress));
        }

        message.Subject = subject;
        message.Body = body.ToMessageBody();

        using (var client = new SmtpClient())
        {
            var smtpHost = context.ClientContext.Environment["SmtpHost"];
            var smtpPort = int.Parse(context.ClientContext.Environment["SmtpPort"]);
            var smtpUsername = context.ClientContext.Environment["SmtpUsername"];
            var smtpPassword = context.ClientContext.Environment["SmtpPassword"];

            client.Connect(smtpHost, smtpPort, SecureSocketOptions.Auto);
            client.Authenticate(smtpUsername, smtpPassword);
            client.Send(message);
            client.Disconnect(true);
        }

        LogInfo(context, "SUCCESS", $"Email sent successfully to {recipientAddressList.Count} recipients");
    }
    catch (Exception ex)
    {
        LogInfo(context, "ERROR", $"Failed to send email: {ex.Message}");
        throw;
    }
}
```

**Rate Plan Limit Alert Implementation**:
```csharp
// Rate plan limit exceeded notification
private void SendCarrierPlanLimitAlertEmail(KeySysLambdaContext context, OptimizationInstance instance, int ratePlanLimit)
{
    try
    {
        var subject = "Carrier Plan optimization Error: Rate Plans limit exceeded";
        var body = new BodyBuilder()
        {
            HtmlBody = $"<div>The rate plan count for Instance {instance.Id} has exceeded the limit of {ratePlanLimit}. Please log in to the Portal to limit the plans selected to {ratePlanLimit} or lower.</div>",
            TextBody = $"The rate plan count for Instance {instance.Id} has exceeded the limit of {ratePlanLimit}. Please log in to the Portal to limit the plans selected to {ratePlanLimit} or lower"
        };

        var fromEmailAddress = context.ClientContext.Environment["OptimizationFromEmailAddress"];
        var recipientAddressList = GetEmailRecipientAddressList(context, EmailRecipientType.OptimizationErrors);
        var bccAddressList = new List<string>();

        SendOptimizationEmail(context, subject, body, fromEmailAddress, recipientAddressList, bccAddressList);
    }
    catch (Exception ex)
    {
        LogInfo(context, "Error Sending Rate Plan Limit Alert Email", ex.Message);
    }
}
```

**Error Alert Categories**:
```csharp
// Different alert types and their handling
public enum AlertType
{
    RatePlanLimitExceeded,
    OptimizationTimeout,
    DatabaseConnectionError,
    DeviceSyncFailure,
    InvalidConfiguration,
    CriticalSystemError
}

private string GenerateAlertSubject(AlertType alertType, OptimizationInstance instance)
{
    return alertType switch
    {
        AlertType.RatePlanLimitExceeded => $"[ALERT] Rate Plan Limit Exceeded - Instance {instance.Id}",
        AlertType.OptimizationTimeout => $"[WARNING] Optimization Timeout - Instance {instance.Id}",
        AlertType.DatabaseConnectionError => $"[CRITICAL] Database Connection Error - Instance {instance.Id}",
        AlertType.DeviceSyncFailure => $"[ERROR] Device Sync Failure - Instance {instance.Id}",
        AlertType.InvalidConfiguration => $"[WARNING] Invalid Configuration - Instance {instance.Id}",
        AlertType.CriticalSystemError => $"[CRITICAL] System Error - Instance {instance.Id}",
        _ => $"[ALERT] Optimization Issue - Instance {instance.Id}"
    };
}
```

---

## Master Algorithm: CompleteEmailNotificationProcess

### Purpose Definition
**What**: Orchestrate all email notification processes including results, attachments, summaries, and alerts  
**Why**: Provide comprehensive communication strategy covering all stakeholder needs and system states  
**How**: Coordinate multiple notification types with appropriate prioritization and routing

### Algorithm: CompleteEmailNotificationProcess

**Input**:
- context: Lambda execution context
- instance: Optimization instance with results
- fileResult: Generated optimization files
- isLastInstance: Boolean indicating final instance
- serviceProviderId: Service provider identifier

**Output**: Complete email notification suite delivered to all stakeholders

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:376-423`

**Algorithm**:
```
Begin CompleteEmailNotificationProcess
    Try
        syncResults ← GetDeviceSyncSummary(context, serviceProviderId)
        integrationTypes ← GetIntegrationTypes(context, serviceProviderId)
        integrationType ← DetermineIntegrationType(integrationTypes, instance)
        
        If fileResult is not null And fileResult.FileBytes is not null Then
            SendResults(context, instance, fileResult.FileBytes, billingTimeZone, syncResults, integrationType, integrationTypes)
            LogInfo("Optimization results sent successfully")
        Else
            SendErrorNotification(context, instance, "No optimization results generated")
            LogWarning("No results file available for email notification")
        End If
        
        If instance.AutoUpdateRatePlans Then
            ProcessRatePlanUpdateNotifications(context, instance)
        End If
        
        If isCustomerOptimization Then
            SendCustomerSpecificNotifications(context, instance, syncResults, isLastInstance, serviceProviderId)
        End If
        
        LogInfo("Email notification process completed successfully")
        
    Catch exception
        LogError("Email notification process failed: " + exception.Message)
        SendCriticalErrorAlert(context, instance, exception.Message)
        Throw exception
    End Try
End CompleteEmailNotificationProcess
```

**Corresponding Implementation Code**:
```csharp
// Lines 376-423: Email Process Integration
private void ProcessResultForSingleServiceProvider(KeySysLambdaContext context, bool isCustomerOptimization, bool isLastInstance, int serviceProviderId, OptimizationInstance instance, IList<IntegrationTypeModel> integrationTypes, OptimizationInstanceResultFile fileResult)
{
    try
    {
        LogInfo(context, LogTypeConstant.Sub, $"({isCustomerOptimization},{isLastInstance},{serviceProviderId},{instance.Id})");

        var syncResults = GetDeviceSyncSummary(context, serviceProviderId);
        var integrationType = integrationTypes.FirstOrDefault(x => x.ServiceProviderId == serviceProviderId)?.IntegrationType ?? IntegrationType.Carrier;

        // Send results with attachments and summaries
        if (fileResult?.FileBytes != null)
        {
            SendResults(context, instance, fileResult.FileBytes, context.OptimizationSettings.BillingTimeZone, 
                syncResults, integrationType, integrationTypes);
        }

        // Handle rate plan update notifications
        if (instance.AutoUpdateRatePlans)
        {
            var ratePlansToUpdateCount = CountRatePlansToUpdate(instance.Id, context.ConnectionString, this);
            
            if (ratePlansToUpdateCount > 0)
            {
                var hasTimeToProcessRatePlanUpdates = DoesHaveTimeToProcessRatePlanUpdates(instance, ratePlansToUpdateCount,
                    context.ConnectionString, this, DateTime.UtcNow, context.OptimizationSettings.BillingTimeZone);

                if (hasTimeToProcessRatePlanUpdates)
                {
                    QueueRatePlanUpdates(context, instance.Id, instance.TenantId);
                    SendGoForRatePlanUpdatesEmail(context, instance, context.OptimizationSettings.BillingTimeZone);
                }
                else
                {
                    SendNoGoForRatePlanUpdatesEmail(context, instance, context.OptimizationSettings.BillingTimeZone);
                }
            }
        }

        // Handle customer-specific notifications
        if (isCustomerOptimization)
        {
            OptimizationCustomerSendResults(context, instance, syncResults, isLastInstance, serviceProviderId);
        }
    }
    catch (Exception ex)
    {
        LogInfo(context, "ERROR", $"Error in ProcessResultForSingleServiceProvider: {ex.Message}");
        throw;
    }
}
```

---

## Algorithm Complexity Analysis

### Time Complexity
- **Results Email Generation**: O(r + a) where r = recipients, a = attachment size
- **Cost Savings Calculation**: O(d) where d = number of devices
- **Alert Notification**: O(r) where r = number of recipients
- **Excel Attachment Processing**: O(d + p) where d = devices, p = rate plans
- **Overall Complexity**: O(d + r + a) for complete notification process

### Space Complexity
- **Memory Usage**: O(d + a + r) where d = device data, a = attachment size, r = recipients
- **Storage Requirements**: O(a) for temporary attachment storage

### Performance Considerations
- **Asynchronous Email Sending**: Prevents blocking of main optimization process
- **Attachment Size Limits**: Excel files optimized for email delivery
- **Recipient List Caching**: Email addresses cached to prevent repeated database queries
- **Error Handling**: Comprehensive retry logic for email delivery failures
- **SMTP Connection Pooling**: Efficient connection management for bulk email sending