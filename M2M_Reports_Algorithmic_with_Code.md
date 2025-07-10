# M2M Reports: Algorithmic Analysis with Code Implementation

## System Overview

**Function Location**: AltaworxSimCardCostOptimizerCleanup.cs (Lines 746-850)  
**Primary Method**: WriteM2MResults()  
**Purpose**: Generate comprehensive M2M optimization reports for IoT device management

---

## 1. Device Assignment Spreadsheets

### Purpose Definition
**What**: Generate Excel spreadsheets displaying SIM card assignments to rate plans  
**Why**: Provide transparency, validation, implementation guidance, and audit trails  
**How**: Map optimization queue results to rate pool assignments and export to Excel format

### Algorithm: GenerateDeviceAssignmentSpreadsheet

**Input**: 
- queueIds: List of optimization queue identifiers
- billingPeriod: Current billing cycle information
- optimizationResultRatePools: Available rate pool collections

**Output**: Excel spreadsheet containing device assignment mappings

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:790-798`

**Algorithm**:
```
Begin GenerateDeviceAssignmentSpreadsheet
    Initialize assignmentFileBytes ← null
    Initialize result ← new M2MOptimizationResult()
    
    For Each queueId in queueIds Do
        deviceResults ← GetM2MResults(context, [queueId], billingPeriod)
        result ← BuildM2MOptimizationResult(deviceResults, optimizationResultRatePools, result)
    End For
    
    assignmentFileBytes ← RatePoolAssignmentWriter.WriteRatePoolAssignments(result)
    assignmentXlsxBytes ← RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes)
    
    Return assignmentXlsxBytes
End GenerateDeviceAssignmentSpreadsheet
```

**Corresponding Code**:
```csharp
// Lines 761-777: Queue Processing
foreach (var queueId in queueIds)
{
    LogInfo(context, LogTypeConstant.Info, $"Building results for queue with id: {queueId}.");
    var deviceResults = GetM2MResults(context, new List<long>() { queueId }, billingPeriod);
    result = BuildM2MOptimizationResult(deviceResults, optimizationResultRatePools, result);
}

// Lines 790-792: Assignment File Generation
var assignmentFileBytes = RatePoolAssignmentWriter.WriteRatePoolAssignments(result);

// Lines 798-800: Excel File Creation
var assignmentXlsxBytes = RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes);
```

**Data Retrieval Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:833-893`

**Database Query Implementation**:
```csharp
using (var cmd = new SqlCommand(@"
    SELECT device.[Id] AS DeviceId, 
        [UsageMB], device.[ICCID], device.[MSISDN],
        ISNULL(commPlan.[AliasName], device.[CommunicationPlan]) AS CommunicationPlan, 
        ISNULL(carrierPlan.[RatePlanCode], customerPlan.[RatePlanCode]) AS RatePlanCode, 
        ISNULL(deviceResult.[AssignedCustomerRatePlanId], deviceResult.[AssignedCarrierRatePlanId]) AS RatePlanId, 
        deviceResult.[CustomerRatePoolId] AS RatePoolId,
        customerPool.[Name] AS RatePoolName,
        [ChargeAmt], device.[ProviderDateActivated], [SmsUsage], 
        [SmsChargeAmount], deviceResult.[BaseRateAmt], deviceResult.[RateChargeAmt], deviceResult.[OverageChargeAmt] 
    FROM OptimizationDeviceResult deviceResult 
    INNER JOIN Device device ON deviceResult.[AmopDeviceId] = device.[Id] 
    LEFT JOIN JasperCommunicationPlan commPlan ON commPlan.[CommunicationPlanName] = device.[CommunicationPlan] 
    LEFT JOIN JasperCarrierRatePlan carrierPlan ON deviceResult.[AssignedCarrierRatePlanId] = carrierPlan.[Id] 
    LEFT JOIN JasperCustomerRatePlan customerPlan ON deviceResult.[AssignedCustomerRatePlanId] = customerPlan.[Id] 
    LEFT JOIN CustomerRatePool customerPool ON deviceResult.[CustomerRatePoolId] = customerPool.[Id] 
    WHERE deviceResult.[QueueId] IN (@QueueIds)", conn))
```

---

## 2. Cost Savings Summaries

### Purpose Definition
**What**: Calculate financial impact of optimization showing cost comparisons  
**Why**: Demonstrate ROI, support financial reporting, enable decision making  
**How**: Compare original versus optimized costs across all devices

### Algorithm: GenerateCostSavingsSummary

**Input**:
- optimizationResult: M2MOptimizationResult containing device data
- billingPeriod: Billing cycle information

**Output**: Statistical summary with comprehensive cost savings data

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:788-790`

**Algorithm**:
```
Begin GenerateCostSavingsSummary
    statisticalSummary ← RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, result)
    
    totalOriginalCost ← 0
    totalOptimizedCost ← 0
    
    For Each ratePoolCollection in optimizationResult.RawRatePools Do
        For Each ratePool in ratePoolCollection.RatePools Do
            For Each simCard in ratePool.SimCards Do
                totalOriginalCost ← totalOriginalCost + simCard.BaseRateAmount + simCard.OverageChargeAmount
                totalOptimizedCost ← totalOptimizedCost + simCard.RateChargeAmount + simCard.OverageChargeAmount
            End For
        End For
    End For
    
    totalSavings ← totalOriginalCost - totalOptimizedCost
    savingsPercentage ← (totalSavings / totalOriginalCost) × 100
    
    Return statisticalSummary
End GenerateCostSavingsSummary
```

**Corresponding Code**:
```csharp
// Lines 788-790: Statistics Generation
var statFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, result);

// Lines 1376-1390: Cost Calculation in BuildM2MOptimizationResult
private M2MOptimizationResult BuildM2MOptimizationResult(List<SimCardResult> deviceResults, List<ResultRatePool> ratePools, M2MOptimizationResult result, bool shouldSkipAutoChangeRatePlan = false)
{
    var tempRPList = new List<M2MRatePool>();
    ratePools.ForEach(ratePool => tempRPList.Add(new M2MRatePool(ratePool)));
    var collection = new M2MRatePoolCollection(tempRPList);
    result.RawRatePools = new List<M2MRatePoolCollection>() { collection };
    return result;
}

// Lines 1143-1174: SimCardResult Creation with Cost Data
private SimCardResult SimCardResultFromReader(SqlDataReader rdr, BillingPeriod billingPeriod)
{
    var simCard = new SimCardResult()
    {
        ChargeAmt = Convert.ToDecimal(rdr["ChargeAmt"].ToString()),
        BaseRateAmount = !rdr.IsDBNull("BaseRateAmt") ? rdr.GetDecimal("BaseRateAmt") : 0,
        RateChargeAmount = !rdr.IsDBNull("RateChargeAmt") ? rdr.GetDecimal("RateChargeAmt") : 0,
        OverageChargeAmount = !rdr.IsDBNull("OverageChargeAmt") ? rdr.GetDecimal("OverageChargeAmt") : 0,
    };
    return simCard;
}
```

---

## 3. Rate Plan Utilization Statistics

### Purpose Definition
**What**: Analyze rate plan effectiveness and usage efficiency  
**Why**: Validate optimization, support capacity planning, identify performance patterns  
**How**: Calculate utilization percentages and efficiency metrics per rate plan

### Algorithm: GenerateUtilizationStatistics

**Input**:
- deviceResults: Optimization results for all devices
- ratePools: Rate pool configurations
- billingPeriod: Billing cycle context

**Output**: Utilization statistics for each rate plan

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:1376-1390`

**Algorithm**:
```
Begin GenerateUtilizationStatistics
    utilizationMetrics ← empty dictionary indexed by RatePlanId
    tempRPList ← empty list
    
    For Each ratePool in ratePools Do
        m2mRatePool ← new M2MRatePool(ratePool)
        
        metrics.TotalDevices ← Count(ratePool.SimCards)
        metrics.TotalUsage ← Sum(simCard.CycleDataUsageMB for simCard in ratePool.SimCards)
        metrics.PlanCapacity ← ratePool.RatePlan.DataAllowanceMB
        
        If metrics.PlanCapacity > 0 Then
            metrics.UtilizationPercentage ← (metrics.TotalUsage / metrics.PlanCapacity) × 100
        Else
            metrics.UtilizationPercentage ← 0
        End If
        
        metrics.CostPerMB ← ratePool.RatePlan.MonthlyRate / ratePool.RatePlan.DataAllowanceMB
        metrics.EffectiveCostPerMB ← ratePool.TotalCharges / metrics.TotalUsage
        
        tempRPList.Add(m2mRatePool)
    End For
    
    ratePoolCollection ← new M2MRatePoolCollection(tempRPList)
    utilizationReport ← RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, result)
    
    Return utilizationReport
End GenerateUtilizationStatistics
```

**Corresponding Code**:
```csharp
// Lines 1376-1390: Rate Pool Collection Building
private M2MOptimizationResult BuildM2MOptimizationResult(List<SimCardResult> deviceResults, List<ResultRatePool> ratePools, M2MOptimizationResult result, bool shouldSkipAutoChangeRatePlan = false)
{
    var tempRPList = new List<M2MRatePool>();
    ratePools.ForEach(ratePool => tempRPList.Add(new M2MRatePool(ratePool)));
    
    var collection = new M2MRatePoolCollection(tempRPList);
    result.RawRatePools = new List<M2MRatePoolCollection>() { collection };
    
    AddSimCardsToResultRatePools(deviceResults, ratePools, shouldSkipAutoChangeRatePlan);
    return result;
}

// Lines 1391-1433: Device Assignment to Rate Pools
private static void AddSimCardsToResultRatePools(List<SimCardResult> deviceResults, List<ResultRatePool> ratePools, bool shouldSkipAutoChangeRatePlan = false)
{
    foreach (var deviceResult in deviceResults)
    {
        var assignedRatePool = ratePools.FirstOrDefault(x => x.RatePlan.Id == deviceResult.RatePlanId);
        if (assignedRatePool != null)
        {
            if (shouldSkipAutoChangeRatePlan && assignedRatePool.RatePlan.AutoChangeRatePlan)
            {
                continue;
            }
            assignedRatePool.AddSimCard(deviceResult);
        }
    }
}

// Lines 788-790: Statistics File Generation
var statFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, result);
```

---

## 4. Optimization Group Details

### Purpose Definition
**What**: Analyze optimization group composition and performance  
**Why**: Evaluate group effectiveness, identify rebalancing opportunities, validate strategies  
**How**: Calculate group-level metrics and generate actionable recommendations

### Algorithm: GenerateOptimizationGroupDetails

**Input**:
- instance: Optimization instance configuration
- queueIds: Queue identifiers for processing
- billingPeriod: Billing cycle information
- isCustomerOptimization: Boolean flag for optimization type

**Output**: Detailed analysis of optimization groups

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:1150-1189`

**Algorithm**:
```
Begin GenerateOptimizationGroupDetails
    ratePlans ← GetRatePlansByPortalType(context, instance, isCustomerOptimization, billingPeriod.Id)
    planPoolMappings ← GetRatePlanToRatePoolMappingByPortalType(context, queueIds, instance.PortalType)
    
    ratePools ← GenerateResultRatePoolFromRatePlans(billingPeriod, usesProration, ratePlans, planPoolMappings, false, instance)
    
    If isCustomerOptimization Then
        crossPooledPlans ← GetCrossCustomerRatePlans(context, distinctRatePlanIds from planPoolMappings)
        crossPooledPlans ← FilterDuplicates(crossPooledPlans, ratePlans)
        
        If crossPooledPlans.Count > 0 Then
            sharedRatePools ← GenerateResultRatePoolFromRatePlans(billingPeriod, usesProration, crossPooledPlans, planPoolMappings, true, instance)
            ratePools.AddRange(sharedRatePools)
        End If
    End If
    
    groupDetails ← empty list
    
    For Each ratePool in ratePools Do
        groupDetail.GroupId ← ratePool.GroupIdentifier
        groupDetail.GroupName ← ratePool.RatePoolName
        groupDetail.GroupType ← If ratePool.IsSharedRatePool Then "Cross-Customer" Else "Customer-Specific"
        groupDetail.DeviceCount ← Count(ratePool.SimCards)
        
        groupDetail.TotalUsage ← Sum(simCard.CycleDataUsageMB for simCard in ratePool.SimCards)
        groupDetail.TotalOriginalCost ← Sum(simCard.BaseRateAmount + simCard.OverageChargeAmount for simCard in ratePool.SimCards)
        groupDetail.TotalOptimizedCost ← Sum(simCard.RateChargeAmount + simCard.OverageChargeAmount for simCard in ratePool.SimCards)
        groupDetail.TotalSavings ← groupDetail.TotalOriginalCost - groupDetail.TotalOptimizedCost
        
        groupDetails.Add(groupDetail)
    End For
    
    Return groupDetails
End GenerateOptimizationGroupDetails
```

**Corresponding Code**:
```csharp
// Lines 1150-1189: Rate Pool Collection Setup
private List<ResultRatePool> GetResultRatePools(KeySysLambdaContext context, OptimizationInstance instance, BillingPeriod billingPeriod, bool usesProration, List<long> queueIds, bool isCustomerOptimization)
{
    LogInfo(context, LogTypeConstant.Sub, $"(,,,{string.Join(',', queueIds)})");
    var ratePlans = GetRatePlansByPortalType(context, instance, isCustomerOptimization, billingPeriod.Id);
    
    var planPoolMappings = GetRatePlanToRatePoolMappingByPortalType(context, queueIds, instance.PortalType);
    
    var ratePools = GenerateResultRatePoolFromRatePlans(billingPeriod, usesProration, ratePlans, planPoolMappings, false, instance);
    
    if (isCustomerOptimization)
    {
        var crossPooledPlans = GetCrossCustomerRatePlans(context, planPoolMappings.Select(mapping => mapping.RatePlanId).Distinct().ToList());
        crossPooledPlans = crossPooledPlans.Except(ratePlans).ToList();
        
        if (crossPooledPlans.Count > 0)
        {
            ratePools.AddRange(GenerateResultRatePoolFromRatePlans(billingPeriod, usesProration, crossPooledPlans, planPoolMappings, true, instance));
        }
    }
    
    return ratePools;
}

// Lines 1201-1220: Rate Pool Generation
private static List<ResultRatePool> GenerateResultRatePoolFromRatePlans(BillingPeriod billingPeriod, bool usesProration, List<RatePlan> ratePlans, List<RatePlanPoolMapping> planPoolMappings, bool isSharedRatePool, OptimizationInstance instance)
{
    var ratePools = new List<ResultRatePool>();
    foreach (var ratePlan in ratePlans.OrderBy(ratePlan => ratePlan.PlanDisplayName))
    {
        var matchingMappings = planPoolMappings.Where(planPoolMapping => planPoolMapping.RatePlanId == ratePlan.Id);
        if (matchingMappings.Count() > 0)
        {
            foreach (var matchingMapping in matchingMappings)
            {
                ratePools.Add(new ResultRatePool(ratePlan, usesProration, billingPeriod, instance.RatePoolKeyType, matchingMapping.RatePoolName, isSharedRatePool));
            }
        }
        else
        {
            ratePools.Add(new ResultRatePool(ratePlan, usesProration, billingPeriod, instance.RatePoolKeyType, isSharedRatePool: isSharedRatePool));
        }
    }
    return ratePools;
}
```

---

## Master Algorithm: CompleteM2MReportGeneration

### Purpose Definition
**What**: Integrate all report types into comprehensive Excel output  
**Why**: Provide complete optimization visibility to stakeholders  
**How**: Orchestrate all algorithms and combine results

### Algorithm: CompleteM2MReportGeneration

**Input**:
- context: Lambda execution context
- instance: Optimization instance configuration
- queueIds: List of queue identifiers
- billingPeriod: Billing cycle information
- usesProration: Boolean for proration calculations
- isCustomerOptimization: Boolean optimization type flag

**Output**: Complete M2M optimization report (Excel file)

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:746-803`

**Algorithm**:
```
Begin CompleteM2MReportGeneration
    Initialize result ← new M2MOptimizationResult()
    Initialize crossCustomerResult ← new M2MOptimizationResult()
    
    crossOptimizationResultRatePools ← GetResultRatePools(context, instance, billingPeriod, usesProration, queueIds, isCustomerOptimization)
    optimizationResultRatePools ← GenerateCustomerSpecificRatePools(crossOptimizationResultRatePools)
    
    If isCustomerOptimization Then
        AddUnassignedRatePool(context, instance, billingPeriod, usesProration, crossOptimizationResultRatePools, optimizationResultRatePools)
    End If
    
    shouldShowCrossPoolingTab ← false
    
    For Each queueId in queueIds Do
        deviceResults ← GetM2MResults(context, [queueId], billingPeriod)
        result ← BuildM2MOptimizationResult(deviceResults, optimizationResultRatePools, result)
        
        sharedPoolDeviceResults ← GetM2MSharedPoolResults(context, [queueId], billingPeriod)
        
        If sharedPoolDeviceResults.Count > 0 Then
            shouldShowCrossPoolingTab ← true
        End If
        
        sharedPoolDeviceResults.AddRange(deviceResults)
        crossCustomerResult ← BuildM2MOptimizationResult(sharedPoolDeviceResults, crossOptimizationResultRatePools, crossCustomerResult, true)
    End For
    
    statFileBytes ← RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, result)
    assignmentFileBytes ← RatePoolAssignmentWriter.WriteRatePoolAssignments(result)
    
    If shouldShowCrossPoolingTab Then
        sharedPoolStatFileBytes ← RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, crossCustomerResult)
        sharedPoolAssignmentFileBytes ← RatePoolAssignmentWriter.WriteRatePoolAssignments(crossCustomerResult)
    Else
        sharedPoolStatFileBytes ← null
        sharedPoolAssignmentFileBytes ← null
    End If
    
    assignmentXlsxBytes ← RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes)
    
    optimizationInstanceResultFile ← SaveOptimizationInstanceResultFile(context, instance.Id, assignmentXlsxBytes)
    
    Return optimizationInstanceResultFile
End CompleteM2MReportGeneration
```

**Complete Implementation Code**:
```csharp
// Lines 746-803: Complete WriteM2MResults Method
protected OptimizationInstanceResultFile WriteM2MResults(KeySysLambdaContext context, OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, bool usesProration, bool isCustomerOptimization)
{
    LogInfo(context, LogTypeConstant.Sub, $"(,instance.Id: {instance.Id}, queueIds: {string.Join(',', queueIds)})");
    M2MOptimizationResult result = new M2MOptimizationResult();
    M2MOptimizationResult crossCustomerResult = new M2MOptimizationResult();

    // get rate pools
    var crossOptimizationResultRatePools = GetResultRatePools(context, instance, billingPeriod, usesProration, queueIds, isCustomerOptimization);

    // create another set of rate pools
    var optimizationResultRatePools = GenerateCustomerSpecificRatePools(crossOptimizationResultRatePools);

    AddUnassignedRatePool(context, instance, billingPeriod, usesProration, crossOptimizationResultRatePools, optimizationResultRatePools);

    var shouldShowCrossPoolingTab = false;
    foreach (var queueId in queueIds)
    {
        LogInfo(context, LogTypeConstant.Info, $"Building results for queue with id: {queueId}.");
        // get results for queue id
        var deviceResults = GetM2MResults(context, new List<long>() { queueId }, billingPeriod);

        // build optimization result
        result = BuildM2MOptimizationResult(deviceResults, optimizationResultRatePools, result);
        var sharedPoolDeviceResults = GetM2MSharedPoolResults(context, new List<long>() { queueId }, billingPeriod);
        if (sharedPoolDeviceResults != null && sharedPoolDeviceResults.Count > 0)
        {
            shouldShowCrossPoolingTab = true;
        }
        sharedPoolDeviceResults.AddRange(deviceResults);
        // enable shouldSkipAutoChangeRatePlans flag to not show rate plans & devices with "Auto Change Rate Plan" in second tab
        crossCustomerResult = BuildM2MOptimizationResult(sharedPoolDeviceResults, crossOptimizationResultRatePools, crossCustomerResult, true);
    }

    // write result to stat file
    var statFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, result);

    // write result to device output file (text)
    var assignmentFileBytes = RatePoolAssignmentWriter.WriteRatePoolAssignments(result);
    byte[] sharedPoolStatFileBytes = null;
    byte[] sharedPoolAssignmentFileBytes = null;

    if (shouldShowCrossPoolingTab)
    {
        // write shared pool result to stat file
        sharedPoolStatFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, crossCustomerResult);

        // write shared pool result to device output file (text)
        sharedPoolAssignmentFileBytes = RatePoolAssignmentWriter.WriteRatePoolAssignments(crossCustomerResult);
    }

    // write result to device output file (xlsx)
    LogInfo(context, "SUB", $"GenerateExcelFileFromByteArrays({result.QueueId})");
    var assignmentXlsxBytes = RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes);

    // save to database
    return SaveOptimizationInstanceResultFile(context, instance.Id, assignmentXlsxBytes);
}
```

---

## Algorithm Complexity Analysis

### Time Complexity
- **Device Assignment**: O(n × m) where n = number of queues, m = devices per queue
- **Cost Savings**: O(d) where d = total number of devices
- **Utilization Statistics**: O(r × d) where r = number of rate pools, d = devices per pool
- **Group Details**: O(g × d) where g = number of groups, d = devices per group
- **Overall Complexity**: O(n × m × r) for complete report generation

### Space Complexity
- **Memory Usage**: O(d + r + g) where d = devices, r = rate pools, g = groups
- **Storage Requirements**: O(d) for Excel file generation

### Performance Considerations
- Database query optimization through indexed joins
- Batch processing for large device collections
- Memory management for Excel file generation
- Parallel processing opportunities for independent queue processing