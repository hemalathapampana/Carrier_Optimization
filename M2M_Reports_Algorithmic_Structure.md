# M2M Reports: Algorithmic Structure Analysis

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

**Algorithm**:
```
Begin GenerateDeviceAssignmentSpreadsheet
    Initialize assignmentFileContainer ← empty
    Initialize optimizationResult ← new M2MOptimizationResult
    
    For Each queueId in queueIds Do
        deviceResults ← RetrieveDeviceResults(queueId, billingPeriod)
        optimizationResult ← BuildOptimizationResult(deviceResults, optimizationResultRatePools, optimizationResult)
    End For
    
    assignmentFile ← CreateAssignmentFile(optimizationResult)
    excelSpreadsheet ← GenerateExcelFile(assignmentFile, statisticalData, sharedPoolData)
    
    Return excelSpreadsheet
End GenerateDeviceAssignmentSpreadsheet
```

### Sub-Algorithm: RetrieveDeviceResults

**Input**: 
- queueIds: Queue identifiers for data retrieval
- billingPeriod: Billing period context

**Output**: List of SimCardResult objects with assignment data

**Algorithm**:
```
Begin RetrieveDeviceResults
    If queueIds is empty Then
        Log warning message
        Return empty list
    End If
    
    deviceList ← empty list
    
    For Each queueId in queueIds Do
        databaseQuery ← ConstructJoinQuery(Device, OptimizationDeviceResult, JasperCommunicationPlan, 
                                          JasperCarrierRatePlan, JasperCustomerRatePlan, CustomerRatePool)
        queryResults ← ExecuteQuery(databaseQuery, queueId)
        
        For Each row in queryResults Do
            simCard ← CreateSimCardResult(row, billingPeriod)
            CalculateBillingPeriodData(simCard, billingPeriod)
            deviceList.Add(simCard)
        End For
    End For
    
    Return deviceList
End RetrieveDeviceResults
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

**Algorithm**:
```
Begin GenerateCostSavingsSummary
    statisticalSummary ← CreateStatisticalAnalysis(optimizationResult, GroupByCommunicationPlan)
    
    totalOriginalCost ← 0
    totalOptimizedCost ← 0
    
    For Each ratePoolCollection in optimizationResult.RawRatePools Do
        For Each ratePool in ratePoolCollection.RatePools Do
            For Each simCard in ratePool.SimCards Do
                totalOriginalCost ← totalOriginalCost + simCard.OriginalCost
                totalOptimizedCost ← totalOptimizedCost + simCard.OptimizedCost
            End For
        End For
    End For
    
    totalSavings ← totalOriginalCost - totalOptimizedCost
    savingsPercentage ← (totalSavings / totalOriginalCost) × 100
    
    summary ← CreateSummaryReport(optimizationResult.TotalDeviceCount, totalOriginalCost, 
                                 totalOptimizedCost, totalSavings, savingsPercentage, billingPeriod)
    
    Return summary
End GenerateCostSavingsSummary
```

### Sub-Algorithm: CalculateDeviceCosts

**Input**: 
- deviceResults: List of device optimization results
- ratePools: Available rate pool configurations

**Output**: Cost calculations for each device

**Algorithm**:
```
Begin CalculateDeviceCosts
    For Each deviceResult in deviceResults Do
        originalCost ← deviceResult.BaseRateAmount + deviceResult.OverageCharges
        
        assignedRatePool ← FindRatePool(ratePools, deviceResult.RatePlanId)
        optimizedCost ← CalculatePoolCost(assignedRatePool, deviceResult.Usage)
        
        savingsAmount ← originalCost - optimizedCost
        savingsPercentage ← (savingsAmount / originalCost) × 100
        
        StoreResults(deviceResult, originalCost, optimizedCost, savingsAmount)
    End For
End CalculateDeviceCosts
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

**Algorithm**:
```
Begin GenerateUtilizationStatistics
    utilizationMetrics ← empty dictionary indexed by RatePlanId
    
    For Each ratePool in ratePools Do
        metrics ← new UtilizationMetrics
        
        metrics.TotalDevices ← Count(ratePool.SimCards)
        metrics.TotalUsage ← Sum(simCard.CycleDataUsage for simCard in ratePool.SimCards)
        metrics.PlanCapacity ← ratePool.RatePlan.DataAllowance
        
        If metrics.PlanCapacity > 0 Then
            metrics.UtilizationPercentage ← (metrics.TotalUsage / metrics.PlanCapacity) × 100
        Else
            metrics.UtilizationPercentage ← 0
        End If
        
        metrics.AverageUsagePerDevice ← metrics.TotalUsage / metrics.TotalDevices
        metrics.UnderutilizedDevices ← Count(devices where usage < (PlanCapacity × 0.3))
        metrics.OverutilizedDevices ← Count(devices where usage > PlanCapacity)
        
        metrics.CostPerMB ← ratePool.RatePlan.MonthlyRate / ratePool.RatePlan.DataAllowance
        metrics.EffectiveCostPerMB ← ratePool.TotalCharges / metrics.TotalUsage
        
        utilizationMetrics[ratePool.RatePlan.Id] ← metrics
    End For
    
    utilizationReport ← GenerateUtilizationReport(utilizationMetrics)
    Return utilizationReport
End GenerateUtilizationStatistics
```

### Sub-Algorithm: CalculateEfficiencyScore

**Input**: utilizationMetrics for a specific rate plan

**Output**: Efficiency score (0-100 scale)

**Algorithm**:
```
Begin CalculateEfficiencyScore
    If utilizationPercentage between 70 and 95 Then
        baseEfficiency ← 100
    Else If utilizationPercentage < 70 Then
        baseEfficiency ← (utilizationPercentage / 70) × 100
    Else
        overage ← utilizationPercentage - 95
        baseEfficiency ← 100 - (overage × 2)
    End If
    
    deviceVariance ← CalculateUsageVariance(devices)
    If deviceVariance > threshold Then
        baseEfficiency ← baseEfficiency × 0.9
    End If
    
    costEfficiencyRatio ← StandardCostPerMB / EffectiveCostPerMB
    efficiencyScore ← baseEfficiency × costEfficiencyRatio
    
    Return Min(efficiencyScore, 100)
End CalculateEfficiencyScore
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

**Algorithm**:
```
Begin GenerateOptimizationGroupDetails
    ratePlans ← GetRatePlansByPortalType(instance, isCustomerOptimization, billingPeriod)
    planPoolMappings ← GetRatePlanMappings(queueIds, instance.PortalType)
    
    ratePools ← GenerateResultRatePools(billingPeriod, ratePlans, planPoolMappings, false, instance)
    
    If isCustomerOptimization Then
        crossPooledPlans ← GetCrossCustomerRatePlans(distinctRatePlanIds from planPoolMappings)
        crossPooledPlans ← FilterDuplicates(crossPooledPlans, ratePlans)
        
        If crossPooledPlans.Count > 0 Then
            sharedRatePools ← GenerateResultRatePools(billingPeriod, crossPooledPlans, planPoolMappings, true, instance)
            ratePools.AddRange(sharedRatePools)
        End If
    End If
    
    groupDetails ← empty list
    
    For Each ratePool in ratePools Do
        groupDetail ← new OptimizationGroupDetail
        
        groupDetail.GroupId ← ratePool.GroupIdentifier
        groupDetail.GroupName ← ratePool.RatePoolName
        groupDetail.GroupType ← If ratePool.IsSharedRatePool Then "Cross-Customer" Else "Customer-Specific"
        groupDetail.DeviceCount ← Count(ratePool.SimCards)
        
        groupDetail.TotalUsage ← Sum(simCard.CycleDataUsage for simCard in ratePool.SimCards)
        groupDetail.AverageUsage ← groupDetail.TotalUsage / groupDetail.DeviceCount
        groupDetail.UsageVariance ← CalculateUsageVariance(ratePool.SimCards)
        
        groupDetail.TotalOriginalCost ← Sum(simCard.OriginalCost for simCard in ratePool.SimCards)
        groupDetail.TotalOptimizedCost ← Sum(simCard.OptimizedCost for simCard in ratePool.SimCards)
        groupDetail.TotalSavings ← groupDetail.TotalOriginalCost - groupDetail.TotalOptimizedCost
        groupDetail.SavingsPercentage ← (groupDetail.TotalSavings / groupDetail.TotalOriginalCost) × 100
        
        groupDetail.OptimizationEffectiveness ← CalculateOptimizationEffectiveness(ratePool)
        groupDetail.GroupCohesion ← CalculateGroupCohesion(ratePool.SimCards)
        groupDetail.RecommendedActions ← GenerateGroupRecommendations(groupDetail)
        
        groupDetails.Add(groupDetail)
    End For
    
    overallSummary ← CreateGroupSummary(groupDetails)
    Return {groupDetails, overallSummary}
End GenerateOptimizationGroupDetails
```

### Sub-Algorithm: CalculateGroupPerformanceMetrics

**Input**: ratePool containing SimCard collections

**Output**: Performance metrics for the group

**Algorithm**:
```
Begin CalculateGroupPerformanceMetrics
    usages ← [simCard.CycleDataUsage for simCard in ratePool.SimCards]
    mean ← Average(usages)
    variance ← Average((usage - mean)² for usage in usages)
    coefficientOfVariation ← SquareRoot(variance) / mean
    usageCohesion ← Max(0, 100 - (coefficientOfVariation × 100))
    
    avgSavingsPerDevice ← ratePool.TotalSavings / ratePool.DeviceCount
    industryBenchmark ← GetIndustryBenchmarkSavings()
    costEffectiveness ← (avgSavingsPerDevice / industryBenchmark) × 100
    
    utilizationScore ← CalculateUtilizationScore(ratePool)
    savingsScore ← (ratePool.SavingsPercentage / 30) × 100
    optimizationScore ← (utilizationScore × 0.4) + (savingsScore × 0.6)
    
    recommendations ← empty list
    
    If usageCohesion < 70 Then
        recommendations.Add("Consider splitting group - high usage variance detected")
    End If
    
    If costEffectiveness < 80 Then
        recommendations.Add("Review rate plan selection for this group")
    End If
    
    If utilizationScore < 60 Then
        recommendations.Add("Group may benefit from different rate plan strategy")
    End If
    
    Return {usageCohesion, costEffectiveness, optimizationScore, recommendations}
End CalculateGroupPerformanceMetrics
```

---

## Master Algorithm: CompleteM2MReportGeneration

### Purpose Definition
**What**: Integrate all report types into comprehensive Excel output  
**Why**: Provide complete optimization visibility to stakeholders  
**How**: Orchestrate all sub-algorithms and combine results

### Algorithm: CompleteM2MReportGeneration

**Input**:
- context: Lambda execution context
- instance: Optimization instance configuration
- queueIds: List of queue identifiers
- billingPeriod: Billing cycle information
- usesProration: Boolean for proration calculations
- isCustomerOptimization: Boolean optimization type flag

**Output**: Complete M2M optimization report (Excel file)

**Algorithm**:
```
Begin CompleteM2MReportGeneration
    Initialize result ← new M2MOptimizationResult
    Initialize crossCustomerResult ← new M2MOptimizationResult
    
    crossOptimizationResultRatePools ← GetResultRatePools(instance, billingPeriod, usesProration, queueIds, isCustomerOptimization)
    optimizationResultRatePools ← GenerateCustomerSpecificRatePools(crossOptimizationResultRatePools)
    
    If isCustomerOptimization Then
        AddUnassignedRatePool(instance, billingPeriod, usesProration, crossOptimizationResultRatePools, optimizationResultRatePools)
    End If
    
    shouldShowCrossPoolingTab ← false
    
    For Each queueId in queueIds Do
        deviceResults ← GetM2MResults(queueId, billingPeriod)
        result ← BuildM2MOptimizationResult(deviceResults, optimizationResultRatePools, result)
        
        sharedPoolDeviceResults ← GetM2MSharedPoolResults(queueId, billingPeriod)
        
        If sharedPoolDeviceResults.Count > 0 Then
            shouldShowCrossPoolingTab ← true
        End If
        
        sharedPoolDeviceResults.AddRange(deviceResults)
        crossCustomerResult ← BuildM2MOptimizationResult(sharedPoolDeviceResults, crossOptimizationResultRatePools, crossCustomerResult, true)
    End For
    
    statFileBytes ← WriteRatePoolStatistics(GroupByCommunicationPlan, result)
    assignmentFileBytes ← WriteRatePoolAssignments(result)
    
    If shouldShowCrossPoolingTab Then
        sharedPoolStatFileBytes ← WriteRatePoolStatistics(GroupByCommunicationPlan, crossCustomerResult)
        sharedPoolAssignmentFileBytes ← WriteRatePoolAssignments(crossCustomerResult)
    Else
        sharedPoolStatFileBytes ← null
        sharedPoolAssignmentFileBytes ← null
    End If
    
    assignmentXlsxBytes ← GenerateExcelFileFromByteArrays(statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes)
    
    optimizationInstanceResultFile ← SaveOptimizationInstanceResultFile(instance.Id, assignmentXlsxBytes)
    
    Return optimizationInstanceResultFile
End CompleteM2MReportGeneration
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