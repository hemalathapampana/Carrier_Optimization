# Assignment Strategies Analysis

## Overview
This document analyzes the four sequential assignment strategies executed by the **AltaworxSimCardCostOptimizer Lambda** function for optimal rate plan assignments based on SimCardGrouping and RemainingAssignmentOrder attributes.

---

## Strategy 1: No Grouping + Largest to Smallest

### Definition
**What**: Processes devices individually by assigning highest data usage devices first to optimal rate plans.  
**Why**: Maximizes cost reduction by prioritizing devices with highest potential savings impact.  
**How**: Sorts devices by descending data usage and assigns each device independently to best available rate plan.

### Algorithm
```
STEP 1: Initialize No Grouping Strategy in AltaworxSimCardCostOptimizer Lambda
    Set SimCardGrouping = NoGrouping for individual device processing
    Set RemainingAssignmentOrder = Largest to Smallest for usage ordering
    Load all SIM cards for optimization instance
    
STEP 2: Sort Devices by Highest Usage First
    Order devices by CycleDataUsageMB in descending order
    Place highest usage devices at beginning of processing queue
    Maintain device list for sequential assignment
    
STEP 3: Process Each Device Individually
    FOR each device in sorted order:
        Calculate cost for each available rate plan
        Consider device-specific usage patterns
        Identify optimal rate plan with lowest cost
        
STEP 4: Assign Optimal Rate Plans
    Assign highest usage device to best rate plan first
    Continue with next highest usage device
    Repeat until all devices are optimally assigned
    Track total cost savings achieved
    
STEP 5: Evaluate Maximum Cost Reduction
    Calculate total cost savings from individual assignments
    Compare against current rate plan costs
    Record optimization results for highest impact devices
    Save assignment results to database
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Assignment strategy configuration in ProcessQueues method
private async Task ProcessQueues(KeySysLambdaContext context, List<long> queueIds, string messageId, bool skipLowerCostCheck, OptimizationChargeType chargeType)
{
    // each run will have 4 sequential calculation with strategy based on a pair of attributes SimCardGrouping and RemainingAssignmentOrder
    // No Grouping + Largest To Smallest
    // No Grouping + Smallest To Largest
    // Group By Communication Plan + Largest To Smallest
    // Group By Communication Plan + Smallest To Largest
    // => stop at the first calculation if there is cache => continue with the next calculation on new lambda instance
    
    var assigner = new RatePoolAssigner(string.Empty, ratePoolCollection, simCards, context.logger, SanityCheckTimeLimit, context.LambdaContext, IsUsingRedisCache,
        instance.PortalType,
        shouldFilterByRatePlanType,
        shouldPoolUsageBetweenRatePlans);
    
    // Execute assignment strategies based on portal type
    assigner.AssignSimCards(GetSimCardGroupingByPortalType(instance.PortalType, instance.IsCustomerOptimization),
                                context.OptimizationSettings.BillingTimeZone,
                                false,
                                false,
                                ratePoolSequences);
}

// Grouping strategy determination based on portal type
private static List<SimCardGrouping> GetSimCardGroupingByPortalType(PortalTypes portalType, bool isCustomerOptimization)
{
    if (portalType == PortalTypes.Mobility || isCustomerOptimization)
    {
        return new List<SimCardGrouping> { SimCardGrouping.NoGrouping };
    }
    else
    {
        return new List<SimCardGrouping> {
                SimCardGrouping.NoGrouping,
                SimCardGrouping.GroupByCommunicationPlan };
    }
}
```

---

## Strategy 2: No Grouping + Smallest to Largest

### Definition
**What**: Processes devices individually by assigning lowest data usage devices first to available rate plans.  
**Why**: Optimizes for plan utilization by filling rate plan capacities with smaller usage devices efficiently.  
**How**: Sorts devices by ascending data usage and assigns each device independently to maximize plan efficiency.

### Algorithm
```
STEP 1: Initialize Smallest First Strategy in AltaworxSimCardCostOptimizer Lambda
    Set SimCardGrouping = NoGrouping for individual device processing
    Set RemainingAssignmentOrder = Smallest to Largest for usage ordering
    Load all SIM cards for optimization instance
    
STEP 2: Sort Devices by Lowest Usage First
    Order devices by CycleDataUsageMB in ascending order
    Place lowest usage devices at beginning of processing queue
    Maintain device list for sequential assignment
    
STEP 3: Process Each Device Individually
    FOR each device in sorted order (lowest usage first):
        Calculate available capacity in each rate plan
        Consider plan utilization efficiency
        Identify rate plan with best utilization match
        
STEP 4: Optimize Plan Utilization
    Assign lowest usage device to maximize plan efficiency first
    Fill rate plan capacities with appropriately sized devices
    Continue with next lowest usage device
    Repeat until optimal utilization achieved
    
STEP 5: Maximize Plan Efficiency
    Calculate total plan utilization across all assignments
    Ensure efficient use of rate plan allowances
    Record optimization results for utilization metrics
    Save assignment results with efficiency scores
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Same RatePoolAssigner handles multiple ordering strategies
// The RemainingAssignmentOrder enumeration controls the sorting direction
// Implementation cycles through both Largest to Smallest and Smallest to Largest
var assigner = new RatePoolAssigner(string.Empty, ratePoolCollection, simCards, context.logger, SanityCheckTimeLimit, context.LambdaContext, IsUsingRedisCache,
    instance.PortalType,
    shouldFilterByRatePlanType,
    shouldPoolUsageBetweenRatePlans);

// Assignment executes all 4 strategies sequentially
// Strategy 2 uses NoGrouping with Smallest to Largest ordering
assigner.AssignSimCards(GetSimCardGroupingByPortalType(instance.PortalType, instance.IsCustomerOptimization),
                            context.OptimizationSettings.BillingTimeZone,
                            false,
                            false,
                            ratePoolSequences);
```

---

## Strategy 3: Group by Communication Plan + Largest to Smallest

### Definition
**What**: Groups devices by communication plan and processes high-usage groups first for rate plan optimization.  
**Why**: Maintains plan consistency within communication groups while prioritizing highest impact optimizations.  
**How**: Aggregates devices by communication plan, sorts groups by total usage, and assigns optimal rate plans per group.

### Algorithm
```
STEP 1: Initialize Communication Plan Grouping in AltaworxSimCardCostOptimizer Lambda
    Set SimCardGrouping = GroupByCommunicationPlan for group processing
    Set RemainingAssignmentOrder = Largest to Smallest for group ordering
    Load all SIM cards and communication plan data
    
STEP 2: Group Devices by Communication Plan
    GROUP devices by communication plan identifier
    Calculate total usage for each communication plan group
    Create group containers maintaining plan consistency
    
STEP 3: Sort Groups by Highest Usage First
    Order communication plan groups by total CycleDataUsageMB descending
    Place highest usage groups at beginning of processing queue
    Maintain group integrity during sorting
    
STEP 4: Process Each Group Sequentially
    FOR each communication plan group in sorted order:
        Keep all devices in group together
        Calculate optimal rate plan for entire group
        Ensure consistent plan assignment within group
        
STEP 5: Maintain Plan Consistency
    Assign same rate plan to all devices in communication group
    Preserve communication plan business rules
    Record group-level optimization results
    Save consistent assignments to database
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Communication plan grouping is available for M2M portal types
private static List<SimCardGrouping> GetSimCardGroupingByPortalType(PortalTypes portalType, bool isCustomerOptimization)
{
    if (portalType == PortalTypes.Mobility || isCustomerOptimization)
    {
        return new List<SimCardGrouping> { SimCardGrouping.NoGrouping };
    }
    else
    {
        // M2M portal type supports both strategies
        return new List<SimCardGrouping> {
                SimCardGrouping.NoGrouping,
                SimCardGrouping.GroupByCommunicationPlan };
    }
}

// Communication plans loaded for M2M optimizations
if (instance.PortalType == PortalTypes.M2M && !instance.IsCustomerOptimization)
{
    commPlans = GetCommPlansForCommGroup(context, queue.CommPlanGroupId);
}

// Strategy 3 uses GroupByCommunicationPlan with Largest to Smallest ordering
// RatePoolAssigner processes groups maintaining communication plan consistency
```

---

## Strategy 4: Group by Communication Plan + Smallest to Largest

### Definition
**What**: Groups devices by communication plan and processes low-usage groups first for bulk assignment optimization.  
**Why**: Optimizes for bulk assignments by handling smaller communication groups efficiently before larger ones.  
**How**: Aggregates devices by communication plan, sorts groups by ascending usage, and assigns rate plans for efficient bulk processing.

### Algorithm
```
STEP 1: Initialize Bulk Assignment Strategy in AltaworxSimCardCostOptimizer Lambda
    Set SimCardGrouping = GroupByCommunicationPlan for group processing
    Set RemainingAssignmentOrder = Smallest to Largest for group ordering
    Load all SIM cards and communication plan data
    
STEP 2: Group Devices by Communication Plan
    GROUP devices by communication plan identifier
    Calculate total usage for each communication plan group
    Create group containers for bulk processing
    
STEP 3: Sort Groups by Lowest Usage First
    Order communication plan groups by total CycleDataUsageMB ascending
    Place lowest usage groups at beginning of processing queue
    Prepare for efficient bulk assignment processing
    
STEP 4: Process Small Groups First for Bulk Efficiency
    FOR each communication plan group in sorted order (smallest first):
        Handle smaller groups quickly for bulk efficiency
        Calculate optimal rate plan for entire group
        Assign consistent rate plans across group members
        
STEP 5: Optimize Bulk Assignments
    Process smaller communication groups efficiently first
    Build momentum with quick bulk assignments
    Handle larger groups after establishing optimization patterns
    Save bulk assignment results to database
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Strategy 4 uses same GroupByCommunicationPlan grouping with different ordering
// The 4 sequential strategies are executed in this order:
// 1. No Grouping + Largest To Smallest
// 2. No Grouping + Smallest To Largest  
// 3. Group By Communication Plan + Largest To Smallest
// 4. Group By Communication Plan + Smallest To Largest

// All strategies execute within single AssignSimCards call
var assigner = new RatePoolAssigner(string.Empty, ratePoolCollection, simCards, context.logger, SanityCheckTimeLimit, context.LambdaContext, IsUsingRedisCache,
    instance.PortalType,
    shouldFilterByRatePlanType,
    shouldPoolUsageBetweenRatePlans);

// RatePoolAssigner internally cycles through all 4 strategies
// Cache mechanism allows continuation if timeout occurs during processing
assigner.AssignSimCards(GetSimCardGroupingByPortalType(instance.PortalType, instance.IsCustomerOptimization),
                            context.OptimizationSettings.BillingTimeZone,
                            false,
                            false,
                            ratePoolSequences);
```

---

## Strategy Selection by Portal Type

### M2M Portal Type
- **Supports**: All 4 strategies (NoGrouping and GroupByCommunicationPlan)
- **Reason**: M2M devices have communication plans that benefit from group consistency
- **Implementation**: `GetSimCardGroupingByPortalType()` returns both grouping options

### Mobility Portal Type  
- **Supports**: Only Strategy 1 and 2 (NoGrouping only)
- **Reason**: Mobility devices are optimized individually without communication plan grouping
- **Implementation**: `GetSimCardGroupingByPortalType()` returns only NoGrouping

### Customer Optimization
- **Supports**: Only Strategy 1 and 2 (NoGrouping only)  
- **Reason**: Customer optimizations focus on individual device assignments
- **Implementation**: `GetSimCardGroupingByPortalType()` returns only NoGrouping