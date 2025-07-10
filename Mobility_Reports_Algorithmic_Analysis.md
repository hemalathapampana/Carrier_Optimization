# Mobility Reports: Algorithmic Analysis with Code Implementation

## System Overview

**Function Location**: AltaworxSimCardCostOptimizerCleanup.cs (Lines 646-715)  
**Primary Method**: WriteMobilityCarrierResults()  
**Purpose**: Generate comprehensive Mobility optimization reports for mobile device management by carriers and optimization groups

---

## 1. Optimization Group Summaries

### Purpose Definition
**What**: Generate comprehensive summaries for each optimization group showing performance metrics, cost effectiveness, and utilization patterns  
**Why**: Enable group-level performance analysis, identify high-performing groups, support strategic decision-making for group rebalancing  
**How**: Aggregate device data by optimization groups, calculate group-level metrics, and generate summary reports with recommendations

### Algorithm: GenerateOptimizationGroupSummaries

**Input**: 
- optimizationGroups: List of optimization group configurations
- deviceResults: Mobility device optimization results
- billingPeriod: Current billing cycle information
- ratePlans: Available rate plan collections

**Output**: List of MobilityCarrierSummaryReportModel objects containing group summaries

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:656-714`

**Algorithm**:
```
Begin GenerateOptimizationGroupSummaries
    Initialize summariesByRatePlans ← empty list of MobilityCarrierSummaryReportModel
    ratePlans ← GetValidRatePlans(serviceProviderId)
    optimizationGroups ← GetValidOptimizationGroupsWithRatePlanIds(serviceProviderId)
    
    deviceResults ← GetMobilityDeviceResults(context, queueIds, billingPeriod)
    deviceResultsByOptimizationGroups ← GroupBy(deviceResults, OptimizationGroupId)
    
    For Each optimizationGroup in optimizationGroups Do
        If deviceResultsByOptimizationGroups.ContainsKey(optimizationGroup.Id) Then
            groupDeviceResults ← deviceResultsByOptimizationGroups[optimizationGroup.Id]
            groupRatePlans ← MapRatePlansToOptimizationGroup(ratePlans, optimizationGroup)
            
            optimizationGroupResultPools ← CreateResultRatePools(groupRatePlans, usesProration, billingPeriod, optimizationGroup.Name)
            
            For Each ratePlan in groupRatePlans Do
                resultPool ← new ResultRatePool(ratePlan, usesProration, billingPeriod, ICCID, optimizationGroup.Name)
                optimizationGroupResultPools.Add(resultPool)
            End For
            
            groupSummaries ← MapToSummariesFromResult(optimizationGroupResultPools, optimizationGroup)
            summariesByRatePlans.AddRange(groupSummaries)
        End If
    End For
    
    Return summariesByRatePlans
End GenerateOptimizationGroupSummaries
```

**Corresponding Code**:
```csharp
// Lines 649-653: Initialize Data Structures
var ratePlans = carrierRatePlanRepository.GetValidRatePlans(ParameterizedLog(context), instance.ServiceProviderId.GetValueOrDefault());
var optimizationGroups = carrierRatePlanRepository.GetValidOptimizationGroupsWithRatePlanIds(ParameterizedLog(context), instance.ServiceProviderId.GetValueOrDefault());

var deviceAssignments = new List<MobilityCarrierAssignmentExportModel>();
var summariesByRatePlans = new List<MobilityCarrierSummaryReportModel>();

// Lines 658-666: Device Results Processing
var deviceResults = optimizationMobilityDeviceRepository.GetMobilityDeviceResults(context, queueIds, billingPeriod);
var deviceResultsByOptimizationGroups = deviceResults
    .Where(x => x.RatePlanTypeId != null && x.OptimizationGroupId != null)
    .GroupBy(x => x.OptimizationGroupId)
    .ToDictionary(x => x.Key, x => x.ToList());

// Lines 668-714: Group Processing Loop
foreach (var optimizationGroup in optimizationGroups)
{
    if (!deviceResultsByOptimizationGroups.TryGetValue(optimizationGroup.Id, out var groupDeviceResults))
    {
        LogInfo(context, CommonConstants.WARNING, string.Format(LogCommonStrings.NO_DEVICE_FOUND_FOR_OPTIMIZATION_GROUP_ID, optimizationGroup.Id));
        continue;
    }
    
    var groupRatePlans = MapRatePlansToOptimizationGroup(ratePlans, optimizationGroup);
    var optimizationGroupResultPools = new List<ResultRatePool>();
    
    foreach (var ratePlan in groupRatePlans)
    {
        optimizationGroupResultPools.Add(new ResultRatePool(ratePlan, usesProration, billingPeriod, ResultRatePoolKeyType.ICCID, optimizationGroup.Name));
    }
    
    summariesByRatePlans.AddRange(MapToSummariesFromResult(optimizationGroupResultPools, optimizationGroup));
}
```

**Summary Mapping Implementation**:
```csharp
// Lines 717-725: Summary Creation
private List<MobilityCarrierSummaryReportModel> MapToSummariesFromResult(List<ResultRatePool> optimizationGroupResultPools, OptimizationGroup optimizationGroup)
{
    var summaries = new List<MobilityCarrierSummaryReportModel>();
    foreach (var resultPool in optimizationGroupResultPools)
    {
        summaries.Add(MobilityCarrierSummaryReportModel.FromResultPool(resultPool, optimizationGroup));
    }
    return summaries;
}
```

---

## 2. Device Assignment by Group

### Purpose Definition
**What**: Generate detailed device-to-group assignments showing which mobile devices belong to specific optimization groups and their rate plan allocations  
**Why**: Provide transparency for group composition, enable validation of grouping logic, support operational implementation of optimization decisions  
**How**: Map each device to its assigned optimization group, track original versus optimized rate plan assignments, export detailed assignment data

### Algorithm: GenerateDeviceAssignmentByGroup

**Input**: 
- optimizationGroups: List of optimization group configurations
- deviceResults: Mobility device optimization results
- billingPeriod: Billing period information
- originalAssignmentCollection: Pre-optimization rate pool assignments

**Output**: List of MobilityCarrierAssignmentExportModel objects containing device assignments

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:668-714`

**Algorithm**:
```
Begin GenerateDeviceAssignmentByGroup
    Initialize deviceAssignments ← empty list of MobilityCarrierAssignmentExportModel
    
    For Each optimizationGroup in optimizationGroups Do
        If deviceResultsByOptimizationGroups.ContainsKey(optimizationGroup.Id) Then
            groupDeviceResults ← deviceResultsByOptimizationGroups[optimizationGroup.Id]
            groupRatePlans ← MapRatePlansToOptimizationGroup(ratePlans, optimizationGroup)
            
            optimizationGroupResultPools ← CreateResultRatePools(groupRatePlans, usesProration, billingPeriod, optimizationGroup.Name)
            originalRatePools ← CreateOriginalRatePools(ratePlans, billingPeriod, usesProration)
            originalAssignmentCollection ← CreateRatePoolCollection(originalRatePools, shouldPoolByOptimizationGroup: true)
            
            For Each deviceResult in groupDeviceResults Do
                AssignDeviceToOriginalPool(originalAssignmentCollection, deviceResult)
                AssignDeviceToOptimizedPool(optimizationGroupResultPools, deviceResult)
            End For
            
            groupDeviceAssignments ← MapToMobilityDeviceAssignmentsFromResult(originalAssignmentCollection, optimizationGroupResultPools, billingPeriod, optimizationGroup)
            deviceAssignments.AddRange(groupDeviceAssignments)
        End If
    End For
    
    Return deviceAssignments
End GenerateDeviceAssignmentByGroup
```

**Corresponding Code**:
```csharp
// Lines 668-714: Device Assignment Processing
foreach (var optimizationGroup in optimizationGroups)
{
    if (!deviceResultsByOptimizationGroups.TryGetValue(optimizationGroup.Id, out var groupDeviceResults))
    {
        LogInfo(context, CommonConstants.WARNING, string.Format(LogCommonStrings.NO_DEVICE_FOUND_FOR_OPTIMIZATION_GROUP_ID, optimizationGroup.Id));
        continue;
    }
    
    var groupRatePlans = MapRatePlansToOptimizationGroup(ratePlans, optimizationGroup);
    var optimizationGroupResultPools = new List<ResultRatePool>();
    
    foreach (var ratePlan in groupRatePlans)
    {
        optimizationGroupResultPools.Add(new ResultRatePool(ratePlan, usesProration, billingPeriod, ResultRatePoolKeyType.ICCID, optimizationGroup.Name));
    }
    
    // Calculate starting cost per device
    var originalRatePools = RatePoolFactory.CreateRatePools(ratePlans, billingPeriod, usesProration, OptimizationChargeType.RateChargeAndOverage);
    var originalAssignmentCollection = RatePoolCollectionFactory.CreateRatePoolCollection(originalRatePools, shouldPoolByOptimizationGroup: true);

    foreach (SimCardResult deviceResult in groupDeviceResults)
    {
        // Add device to the original assignment collection
        foreach (var ratePool in originalAssignmentCollection.RatePools)
        {
            if (ratePool.RatePlan.Id == deviceResult.StartingRatePlanId)
            {
                ratePool.AddSimCard(deviceResult.ToSimCard());
                break;
            }
        }
        
        // Add device to the final result collection
        foreach (var ratePool in optimizationGroupResultPools)
        {
            if (ratePool.RatePlan.Id == deviceResult.RatePlanId)
            {
                ratePool.AddSimCard(deviceResult);
                break;
            }
        }
    }
    
    deviceAssignments.AddRange(MapToMobilityDeviceAssignmentsFromResult(originalAssignmentCollection, optimizationGroupResultPools, billingPeriod, optimizationGroup));
}
```

**Device Assignment Mapping Implementation**:
```csharp
// Lines 727-745: Device Assignment Mapping
private List<MobilityCarrierAssignmentExportModel> MapToMobilityDeviceAssignmentsFromResult(RatePoolCollection originalAssignmentCollection, List<ResultRatePool> optimizationGroupResultPools, BillingPeriod billingPeriod, OptimizationGroup optimizationGroup)
{
    var deviceAssignments = new List<MobilityCarrierAssignmentExportModel>();
    foreach (var resultPool in optimizationGroupResultPools)
    {
        foreach (var sim in resultPool.SimCards)
        {
            var originalRatePool = originalAssignmentCollection.RatePools.FirstOrDefault(x => x.SimCards.TryGetValue(sim.Key, out var _));
            if (originalRatePool == null)
            {
                continue;
            }
            var deviceAssignment = MobilityCarrierAssignmentExportModel.FromSimCardResult(sim.Value, originalRatePool?.RatePlan, resultPool.RatePlan, billingPeriod.BillingPeriodStart, optimizationGroup.Name);
            deviceAssignments.Add(deviceAssignment);
        }
    }
    return deviceAssignments;
}
```

**Database Query for Mobility Device Results**:
```csharp
// Lines 898-938: Mobility Device Results Query
private List<SimCardResult> GetMobilityResults(KeySysLambdaContext context, List<long> queueIds, BillingPeriod billingPeriod)
{
    var simCards = new List<SimCardResult>();

    foreach (var queueId in queueIds)
    {
        using (var conn = new SqlConnection(context.ConnectionString))
        {
            conn.Open();

            using (var cmd = new SqlCommand(
                @"SELECT jd.Id AS DeviceId, UsageMB, jd.ICCID, jd.MSISDN, '' AS CommunicationPlan,
                            ISNULL(jcarr_rp.RatePlanCode,  jcust_rp.RatePlanCode) AS RatePlanCode,
                            ISNULL(odr.AssignedCustomerRatePlanId, odr.AssignedCarrierRatePlanId) AS RatePlanId, 
                            odr.CustomerRatePoolId AS RatePoolId,
                            cust_pool.[Name] AS RatePoolName,
                            ChargeAmt, jd.ProviderDateActivated, SmsUsage, SmsChargeAmount,
                            odr.BaseRateAmt, odr.RateChargeAmt, odr.OverageChargeAmt 
                        FROM OptimizationMobilityDeviceResult odr 
                        INNER JOIN MobilityDevice jd ON odr.AmopDeviceId = jd.Id 
                        LEFT JOIN JasperCarrierRatePlan jcarr_rp ON odr.AssignedCarrierRatePlanId = jcarr_rp.Id 
                        LEFT JOIN JasperCustomerRatePlan jcust_rp ON odr.AssignedCustomerRatePlanId = jcust_rp.Id 
                        LEFT JOIN CustomerRatePool cust_pool ON odr.CustomerRatePoolId = cust_pool.Id 
                        WHERE QueueId = @queueId", conn))
            {
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("@queueId", queueId);

                var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var simCard = SimCardResultFromReader(rdr, billingPeriod);
                    simCards.Add(simCard);
                }
            }
        }
    }

    return simCards;
}
```

---

## 3. Cost Analysis by Carrier

### Purpose Definition
**What**: Generate comprehensive cost analysis reports segmented by carrier, showing optimization savings, rate plan effectiveness, and financial impact  
**Why**: Enable carrier-specific performance evaluation, support carrier contract negotiations, provide ROI analysis for carrier relationships  
**How**: Aggregate optimization results by carrier, calculate cost metrics, compare original versus optimized costs, generate financial impact reports

### Algorithm: GenerateCostAnalysisByCarrier

**Input**: 
- deviceAssignments: List of device assignments with carrier information
- summariesByRatePlans: Summary data grouped by rate plans
- billingPeriod: Billing period context
- carrierInformation: Carrier configuration data

**Output**: Excel spreadsheet with comprehensive cost analysis by carrier

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:715-716`

**Algorithm**:
```
Begin GenerateCostAnalysisByCarrier
    Initialize carrierCostAnalysis ← empty dictionary indexed by CarrierId
    
    For Each deviceAssignment in deviceAssignments Do
        carrierId ← ExtractCarrierId(deviceAssignment.RatePlan)
        
        If Not carrierCostAnalysis.ContainsKey(carrierId) Then
            carrierCostAnalysis[carrierId] ← new CarrierCostMetrics()
        End If
        
        carrierMetrics ← carrierCostAnalysis[carrierId]
        
        carrierMetrics.TotalDevices ← carrierMetrics.TotalDevices + 1
        carrierMetrics.OriginalCost ← carrierMetrics.OriginalCost + deviceAssignment.OriginalCost
        carrierMetrics.OptimizedCost ← carrierMetrics.OptimizedCost + deviceAssignment.OptimizedCost
        carrierMetrics.TotalSavings ← carrierMetrics.OriginalCost - carrierMetrics.OptimizedCost
        carrierMetrics.SavingsPercentage ← (carrierMetrics.TotalSavings / carrierMetrics.OriginalCost) × 100
        
        UpdateRatePlanMetrics(carrierMetrics, deviceAssignment)
    End For
    
    For Each summaryReport in summariesByRatePlans Do
        carrierId ← ExtractCarrierId(summaryReport.RatePlan)
        
        If carrierCostAnalysis.ContainsKey(carrierId) Then
            carrierMetrics ← carrierCostAnalysis[carrierId]
            UpdateCarrierSummaryMetrics(carrierMetrics, summaryReport)
        End If
    End For
    
    assignmentXlsxBytes ← WriteOptimizationResultSheet(deviceAssignments, summariesByRatePlans)
    
    Return assignmentXlsxBytes
End GenerateCostAnalysisByCarrier
```

**Corresponding Code**:
```csharp
// Lines 715-716: Excel Report Generation
var assignmentXlsxBytes = RatePoolAssignmentWriter.WriteOptimizationResultSheet(deviceAssignments, summariesByRatePlans);
return SaveOptimizationInstanceResultFile(context, instance.Id, assignmentXlsxBytes);

// Lines 592-645: Mobility Results Processing (Alternative Method)
protected OptimizationInstanceResultFile WriteMobilityResults(KeySysLambdaContext context, OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, bool usesProration, bool isCustomerOptimization)
{
    LogInfo(context, LogTypeConstant.Sub, $"(,{instance.Id},{string.Join(',', queueIds)})");
    var result = new MobilityOptimizationResult();
    var crossCustomerResult = new MobilityOptimizationResult();

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
        var deviceResults = GetMobilityResults(context, new List<long>() { queueId }, billingPeriod);

        // build optimization result
        result = BuildMobilityOptimizationResult(deviceResults, optimizationResultRatePools, result);
        var sharedPooldeviceResults = GetMobilitySharedPoolResults(context, new List<long>() { queueId }, billingPeriod);
        if (sharedPooldeviceResults != null && sharedPooldeviceResults.Count > 0)
        {
            shouldShowCrossPoolingTab = true;
        }
        sharedPooldeviceResults.AddRange(deviceResults);
        crossCustomerResult = BuildMobilityOptimizationResult(sharedPooldeviceResults, crossOptimizationResultRatePools, crossCustomerResult, true);
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

**Mobility Optimization Result Building**:
```csharp
// Lines 1362-1375: Mobility Result Building
private MobilityOptimizationResult BuildMobilityOptimizationResult(List<SimCardResult> deviceResults, List<ResultRatePool> ratePools, MobilityOptimizationResult result, bool shouldSkipAutoChangeRatePlan = false)
{
    var tempRPList = new List<MobilityRatePool>();
    ratePools.ForEach(ratePool => tempRPList.Add(new MobilityRatePool(ratePool)));

    var collection = new MobilityRatePoolCollection(tempRPList);
    result.RawRatePools = new List<MobilityRatePoolCollection>() { collection };

    AddSimCardsToResultRatePools(deviceResults, ratePools, shouldSkipAutoChangeRatePlan);
    return result;
}
```

---

## Master Algorithm: CompleteMobilityReportGeneration

### Purpose Definition
**What**: Integrate all mobility report types into comprehensive Excel output with carrier-specific analysis  
**Why**: Provide complete mobility optimization visibility organized by carriers and optimization groups  
**How**: Orchestrate all mobility algorithms and combine results into structured Excel reports

### Algorithm: CompleteMobilityReportGeneration

**Input**:
- context: Lambda execution context
- instance: Optimization instance configuration
- queueIds: List of queue identifiers
- billingPeriod: Billing cycle information
- usesProration: Boolean for proration calculations

**Output**: Complete Mobility optimization report (Excel file)

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:646-716`

**Algorithm**:
```
Begin CompleteMobilityReportGeneration
    Initialize deviceAssignments ← empty list of MobilityCarrierAssignmentExportModel
    Initialize summariesByRatePlans ← empty list of MobilityCarrierSummaryReportModel
    
    ratePlans ← GetValidRatePlans(serviceProviderId)
    optimizationGroups ← GetValidOptimizationGroupsWithRatePlanIds(serviceProviderId)
    
    deviceResults ← GetMobilityDeviceResults(context, queueIds, billingPeriod)
    ValidateDeviceResults(deviceResults)
    deviceResultsByOptimizationGroups ← GroupDevicesByOptimizationGroup(deviceResults)
    
    For Each optimizationGroup in optimizationGroups Do
        If deviceResultsByOptimizationGroups.ContainsKey(optimizationGroup.Id) Then
            groupDeviceResults ← deviceResultsByOptimizationGroups[optimizationGroup.Id]
            groupRatePlans ← MapRatePlansToOptimizationGroup(ratePlans, optimizationGroup)
            
            optimizationGroupResultPools ← CreateOptimizationGroupResultPools(groupRatePlans, usesProration, billingPeriod, optimizationGroup)
            originalAssignmentCollection ← CreateOriginalAssignmentCollection(ratePlans, billingPeriod, usesProration)
            
            ProcessDeviceAssignments(groupDeviceResults, originalAssignmentCollection, optimizationGroupResultPools)
            
            groupDeviceAssignments ← MapToMobilityDeviceAssignmentsFromResult(originalAssignmentCollection, optimizationGroupResultPools, billingPeriod, optimizationGroup)
            groupSummaries ← MapToSummariesFromResult(optimizationGroupResultPools, optimizationGroup)
            
            deviceAssignments.AddRange(groupDeviceAssignments)
            summariesByRatePlans.AddRange(groupSummaries)
        End If
    End For
    
    assignmentXlsxBytes ← WriteOptimizationResultSheet(deviceAssignments, summariesByRatePlans)
    optimizationInstanceResultFile ← SaveOptimizationInstanceResultFile(instance.Id, assignmentXlsxBytes)
    
    Return optimizationInstanceResultFile
End CompleteMobilityReportGeneration
```

**Complete Implementation Code**:
```csharp
// Lines 646-716: Complete WriteMobilityCarrierResults Method
protected OptimizationInstanceResultFile WriteMobilityCarrierResults(KeySysLambdaContext context, OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, bool usesProration)
{
    LogInfo(context, CommonConstants.SUB, $"(,{instance.Id},{string.Join(',', queueIds)})");

    // Get Rate Plans from QueueIds
    var ratePlans = carrierRatePlanRepository.GetValidRatePlans(ParameterizedLog(context), instance.ServiceProviderId.GetValueOrDefault());
    // Get Optimization Groups
    var optimizationGroups = carrierRatePlanRepository.GetValidOptimizationGroupsWithRatePlanIds(ParameterizedLog(context), instance.ServiceProviderId.GetValueOrDefault());

    var deviceAssignments = new List<MobilityCarrierAssignmentExportModel>();
    var summariesByRatePlans = new List<MobilityCarrierSummaryReportModel>();
    // Get the device results
    var deviceResults = optimizationMobilityDeviceRepository.GetMobilityDeviceResults(context, queueIds, billingPeriod);
    if (deviceResults.Any(x => x.RatePlanTypeId == null || x.OptimizationGroupId == null))
    {
        LogInfo(context, CommonConstants.ERROR, string.Format(LogCommonStrings.ERROR_NULL_RATE_PLAN_TYPE_ID_OPTIMIZATION_GROUP_ID, string.Join(',', deviceResults.Select(x => x.ICCID))));
    }
    var deviceResultsByOptimizationGroups = deviceResults
        .Where(x => x.RatePlanTypeId != null && x.OptimizationGroupId != null)
        .GroupBy(x => x.OptimizationGroupId)
        .ToDictionary(x => x.Key, x => x.ToList());
    // Map the devices to each optimization group
    foreach (var optimizationGroup in optimizationGroups)
    {
        if (!deviceResultsByOptimizationGroups.TryGetValue(optimizationGroup.Id, out var groupDeviceResults))
        {
            LogInfo(context, CommonConstants.WARNING, string.Format(LogCommonStrings.NO_DEVICE_FOUND_FOR_OPTIMIZATION_GROUP_ID, optimizationGroup.Id));
            continue;
        }
        var groupRatePlans = MapRatePlansToOptimizationGroup(ratePlans, optimizationGroup);
        var optimizationGroupResultPools = new List<ResultRatePool>();
        foreach (var ratePlan in groupRatePlans)
        {
            optimizationGroupResultPools.Add(new ResultRatePool(ratePlan, usesProration, billingPeriod, ResultRatePoolKeyType.ICCID, optimizationGroup.Name));
        }
        // Calculate starting cost per device
        // Might need to increase RAM is we are calculating again
        var originalRatePools = RatePoolFactory.CreateRatePools(ratePlans, billingPeriod, usesProration, OptimizationChargeType.RateChargeAndOverage);
        var originalAssignmentCollection = RatePoolCollectionFactory.CreateRatePoolCollection(originalRatePools, shouldPoolByOptimizationGroup: true);

        foreach (SimCardResult deviceResult in groupDeviceResults)
        {
            // Add device to the original assignment collection
            foreach (var ratePool in originalAssignmentCollection.RatePools)
            {
                if (ratePool.RatePlan.Id == deviceResult.StartingRatePlanId)
                {
                    ratePool.AddSimCard(deviceResult.ToSimCard());
                    break;
                }
            }
            // Add device to the final result collection
            foreach (var ratePool in optimizationGroupResultPools)
            {
                if (ratePool.RatePlan.Id == deviceResult.RatePlanId)
                {
                    ratePool.AddSimCard(deviceResult);
                    break;
                }
            }
        }
        deviceAssignments.AddRange(MapToMobilityDeviceAssignmentsFromResult(originalAssignmentCollection, optimizationGroupResultPools, billingPeriod, optimizationGroup));
        summariesByRatePlans.AddRange(MapToSummariesFromResult(optimizationGroupResultPools, optimizationGroup));
    }
    // Write result to device output file (xlsx)
    var assignmentXlsxBytes = RatePoolAssignmentWriter.WriteOptimizationResultSheet(deviceAssignments, summariesByRatePlans);

    // save to database
    return SaveOptimizationInstanceResultFile(context, instance.Id, assignmentXlsxBytes);
}
```

---

## Algorithm Complexity Analysis

### Time Complexity
- **Optimization Group Summaries**: O(g × r) where g = number of groups, r = rate plans per group
- **Device Assignment by Group**: O(g × d) where g = number of groups, d = devices per group
- **Cost Analysis by Carrier**: O(d + s) where d = device assignments, s = summary reports
- **Overall Complexity**: O(g × d × r) for complete mobility report generation

### Space Complexity
- **Memory Usage**: O(g + d + r) where g = groups, d = devices, r = rate plans
- **Storage Requirements**: O(d) for Excel file generation

### Performance Considerations
- Optimization group-based processing enables parallel execution
- Device results are pre-filtered and grouped for efficient processing
- Rate plan mapping is cached per optimization group
- Excel generation optimized for large device collections