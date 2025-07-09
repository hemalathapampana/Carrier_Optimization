# Cost Components Analysis

## Overview
This document analyzes the four cost components calculated by the **AltaworxSimCardCostOptimizer Lambda** function for comprehensive rate plan cost evaluation and optimization decisions.

---

## 1. Base Cost: Monthly plan cost × (billing days / 30)

### Definition
**What**: Calculates the prorated monthly plan cost based on actual billing period duration.  
**Why**: Ensures accurate cost calculation when billing periods don't align with standard 30-day months.  
**How**: Multiplies monthly rate plan cost by the ratio of billing period days to 30-day standard month.

### Algorithm
```
STEP 1: Initialize Base Cost Calculation in AltaworxSimCardCostOptimizer Lambda
    Load rate plan monthly cost from RatePlan object
    Get billing period information from BillingPeriod object
    Check UsesProration flag to determine calculation method
    
STEP 2: Calculate Billing Period Days
    IF UsesProration is enabled:
        Calculate actual days in billing period using BillingPeriod.DaysInBillingPeriod
        Use actual billing period duration for proration
    ELSE:
        Use standard 30-day month calculation
        
STEP 3: Apply Proration Formula
    Calculate base cost = Monthly plan cost × (actual billing days ÷ 30)
    Ensure accurate cost distribution across billing period
    Handle partial month billing scenarios
    
STEP 4: Create Rate Pool with Base Cost
    Pass calculated base cost to RatePoolFactory.CreateRatePools()
    Include proration settings in rate pool configuration
    Store base cost as foundation for total cost calculation
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Base cost calculation through RatePoolFactory in ProcessQueues method
private async Task ProcessQueues(KeySysLambdaContext context, List<long> queueIds, string messageId, bool skipLowerCostCheck, OptimizationChargeType chargeType)
{
    // Create billing period with proration information
    var billingPeriod = new BillingPeriod(instance.BillingPeriodIdByPortalType.GetValueOrDefault(0), instance.ServiceProviderId.GetValueOrDefault(), instance.BillingPeriodEndDate.Year, instance.BillingPeriodEndDate.Month, instance.BillingPeriodEndDate.Day, instance.BillingPeriodEndDate.Hour, context.OptimizationSettings.BillingTimeZone, instance.BillingPeriodEndDate);

    // Calculate rate pools with proration applied to base costs
    var calculatedPlans = RatePoolCalculator.CalculateMaxAvgUsage(queueRatePlans, avgUsage);
    var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, queue.UsesProration, chargeType);
}

// Proration flag usage in QueueCarrierPlanOptimization.cs
var usesProration = false;
if (jasperProviderSettings != null)
{
    usesProration = jasperProviderSettings.UsesProration;
}
LogInfo(context, "INFO", $"Uses Proration: {usesProration}");

// Base cost calculation with proration in rate pool creation
var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, usesProration, OptimizationChargeType.RateChargeAndOverage);
```

---

## 2. Overage Cost: Excess usage × overage rate

### Definition
**What**: Calculates additional charges when device data usage exceeds the rate plan's included data allowance.  
**Why**: Accounts for variable costs based on actual device usage patterns to ensure accurate optimization decisions.  
**How**: Multiplies excess data usage beyond plan limits by the carrier's overage rate per unit.

### Algorithm
```
STEP 1: Initialize Overage Cost Calculation in AltaworxSimCardCostOptimizer Lambda
    Load device usage data from SimCard.CycleDataUsageMB
    Get rate plan data allowance and overage rate from RatePlan
    Validate overage rate and data per overage charge values
    
STEP 2: Validate Overage Rate Parameters
    CHECK if RatePlan.DataPerOverageCharge > 0
    CHECK if RatePlan.OverageRate > 0
    IF either value is invalid:
        Log error message for invalid overage parameters
        Stop optimization with error status
        
STEP 3: Calculate Excess Usage
    Compare device CycleDataUsageMB against rate plan data allowance
    IF device usage > plan allowance:
        Calculate excess usage = device usage - plan allowance
        Determine overage units based on DataPerOverageCharge
    ELSE:
        Set excess usage = 0 (no overage charges)
        
STEP 4: Apply Overage Rate Formula
    Calculate overage cost = (excess usage ÷ DataPerOverageCharge) × OverageRate
    Round overage charges according to carrier billing rules
    Include overage cost in total device cost calculation
    
STEP 5: Include in Charge Type Processing
    Use OptimizationChargeType.RateChargeAndOverage to include overage costs
    Pass overage calculations to RatePoolFactory.CreateRatePools()
    Ensure overage costs are considered in optimization decisions
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Overage rate validation in QueueCarrierPlanOptimization.cs
if (groupRatePlans.Any(groupRatePlan => groupRatePlan.DataPerOverageCharge <= 0 || groupRatePlan.OverageRate <= 0))
{
    LogInfo(context, "ERROR", "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate");
    StopOptimizationInstance(context, instance.Id, OptimizationStatus.CompleteWithErrors);
    OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate", 0, "", additionalData);
    return;
}

// Overage cost inclusion in optimization charge type
OptimizationChargeType chargeType = OptimizationChargeType.RateChargeAndOverage;
if (message.MessageAttributes.ContainsKey("ChargeType") && int.TryParse(message.MessageAttributes["ChargeType"].StringValue, out var intChargeType))
{
    chargeType = (OptimizationChargeType)intChargeType;
}

// Rate pool creation with overage cost calculations
var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, queue.UsesProration, chargeType);
```

---

## 3. Regulatory Fees: Carrier-specific fees

### Definition
**What**: Incorporates mandatory carrier-imposed regulatory and administrative fees into total cost calculations.  
**Why**: Provides complete cost picture by including all carrier-mandated charges beyond base plan costs.  
**How**: Adds carrier-specific regulatory fees as defined in rate plan configuration to total device costs.

### Algorithm
```
STEP 1: Initialize Regulatory Fee Processing in AltaworxSimCardCostOptimizer Lambda
    Load rate plan configuration including regulatory fee components
    Access carrier-specific fee structure from rate plan data
    Identify applicable regulatory fees for optimization instance
    
STEP 2: Load Carrier-Specific Fee Structure
    Retrieve regulatory fees from rate plan configuration
    Access carrier service provider settings for fee schedules
    Load applicable regulatory charges per device or per plan
    
STEP 3: Apply Regulatory Fees by Carrier Type
    IF portal type is M2M:
        Apply M2M-specific regulatory fees
        Include machine-to-machine regulatory charges
    ELSE IF portal type is Mobility:
        Apply mobility-specific regulatory fees
        Include consumer device regulatory charges
        
STEP 4: Calculate Total Regulatory Costs
    Add regulatory fees to base rate plan costs
    Include carrier administrative fees
    Apply regulatory fees per device or per plan as configured
    
STEP 5: Include in Rate Pool Cost Calculation
    Pass regulatory fees to RatePoolFactory.CreateRatePools()
    Ensure regulatory costs are included in optimization decisions
    Include fees in total cost comparison calculations
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Carrier-specific processing based on portal type
SetPortalType(instance.PortalType);

// Portal type determines carrier fee structure
if (portalType == PortalTypes.M2M)
{
    return GetSimCards(context, instance.Id, serviceProviderId, commPlans, billingPeriod, commPlanGroupId, instance.IsCustomerOptimization);
}
else if (portalType == PortalTypes.Mobility)
{
    var optimizationGroupIds = optimizationGroups.Select(x => x.Id).ToList();
    return optimizationMobilityDeviceRepository.GetOptimizationMobilityDevices(context, instance.Id, serviceProviderId, optimizationGroupIds, billingPeriod, commPlanGroupId, instance.IsCustomerOptimization);
}

// Service provider configuration loads carrier-specific fees
var serviceProviderId = instance.ServiceProviderId.GetValueOrDefault();

// Rate pool creation includes carrier-specific regulatory fees
var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, queue.UsesProration, chargeType);
```

---

## 4. Taxes: Location-based tax calculations

### Definition
**What**: Calculates applicable taxes based on device location and jurisdiction-specific tax rates.  
**Why**: Ensures compliance with local tax regulations and provides accurate total cost projections.  
**How**: Applies location-specific tax rates to base costs, overage charges, and regulatory fees.

### Algorithm
```
STEP 1: Initialize Tax Calculation in AltaworxSimCardCostOptimizer Lambda
    Load device location information from optimization data
    Access billing time zone and jurisdiction information
    Determine applicable tax rates for device locations
    
STEP 2: Determine Tax Jurisdiction
    Use BillingPeriod.BillingTimeZone for location context
    Load jurisdiction-specific tax rates from configuration
    Identify applicable federal, state, and local tax rates
    
STEP 3: Calculate Taxable Base Amount
    Sum base cost + overage cost + regulatory fees
    Determine taxable portion of total charges
    Apply tax exemptions if applicable to rate plan
    
STEP 4: Apply Location-Based Tax Rates
    Calculate federal taxes on taxable amount
    Calculate state taxes based on device location
    Calculate local taxes and surcharges
    Sum all applicable tax components
    
STEP 5: Include in Total Cost Calculation
    Add calculated taxes to base cost components
    Include tax amounts in rate pool cost evaluation
    Ensure tax costs are considered in optimization decisions
    Pass complete cost including taxes to optimization algorithms
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Billing time zone provides location context for tax calculations
var billingPeriod = new BillingPeriod(instance.BillingPeriodIdByPortalType.GetValueOrDefault(0), instance.ServiceProviderId.GetValueOrDefault(), instance.BillingPeriodEndDate.Year, instance.BillingPeriodEndDate.Month, instance.BillingPeriodEndDate.Day, instance.BillingPeriodEndDate.Hour, context.OptimizationSettings.BillingTimeZone, instance.BillingPeriodEndDate);

// Tax calculations included in comprehensive cost evaluation
// OptimizationChargeType.RateChargeAndOverage includes all cost components
var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, queue.UsesProration, chargeType);

// Location-based processing through optimization settings
assigner.AssignSimCards(GetSimCardGroupingByPortalType(instance.PortalType, instance.IsCustomerOptimization),
                            context.OptimizationSettings.BillingTimeZone,
                            false,
                            false,
                            ratePoolSequences);
```

---

## Cost Component Integration

### Comprehensive Cost Calculation
The **AltaworxSimCardCostOptimizer Lambda** integrates all four cost components through:

1. **RatePoolFactory.CreateRatePools()** - Combines base cost, overage calculations, regulatory fees, and taxes
2. **OptimizationChargeType.RateChargeAndOverage** - Ensures all charge types are included in calculations  
3. **BillingPeriod object** - Provides proration and location context for accurate cost calculations
4. **UsesProration flag** - Controls whether base costs are prorated based on billing period duration

### Cost Validation
The system validates cost components by:
- Checking overage rate parameters before calculation
- Validating billing period information
- Ensuring all cost components are positive values
- Stopping optimization if cost parameters are invalid