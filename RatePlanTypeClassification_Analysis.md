# Rate Plan Type Classification - Key Operations Analysis

## Overview

The Rate Plan Type Classification system is a core component of the Altaworx SIM Card Cost Optimization System that handles the categorization, grouping, and intelligent distribution of rate plans based on their types (data, voice, SMS, etc.) to ensure optimal sequence generation and assignment testing.

## What, Who, How Analysis

### What

**What do the Key Operations do?**
- **Type Classification**: Groups rate plans by their service type (data, voice, SMS, messaging, etc.)
- **Diversity Maintenance**: Ensures each generated sequence covers different plan types for comprehensive optimization
- **Balanced Generation**: Creates sequences with appropriate type distribution to avoid over-concentration in single categories
- **Type-Specific Optimization**: Applies specialized business rules and cost calculations specific to each plan type

### Who

**Who uses the Type Classification Operations?**
- **GenerateRatePoolSequencesByRatePlanTypes()** - Primary function that orchestrates type-based sequence generation
- **Mobility optimization processes** - Uses type classification for mobile device rate plan assignment
- **Rate plan administrators** - Configure type-specific rules and constraints for different service types
- **Optimization engineers** - Monitor type distribution effectiveness and adjust classification algorithms
- **Business analysts** - Review type-based optimization results and cost savings by service category
- **Telecommunications service providers** - Benefit from optimized rate plan assignments across different service types

### How

**How do the Type Classification Operations work?**
- **Classification Phase**: Analyzes rate plans and categorizes them by service type using plan metadata and service characteristics
- **Diversity Analysis**: Evaluates existing sequences to identify type gaps and ensure comprehensive coverage
- **Balanced Distribution**: Applies algorithms to maintain proportional representation of each plan type across sequences
- **Type-Specific Processing**: Executes specialized optimization logic tailored to the unique characteristics of each service type

## Algorithm Description

### Core Type Classification Algorithm

The Type Classification system implements a **multi-phase categorization and distribution algorithm** with the following characteristics:

```
INPUT: RatePoolCollection (containing rate pools with mixed plan types)

ALGORITHM:

PHASE 1: TYPE CLASSIFICATION
1. Analyze each rate plan's service characteristics:
   - Data plan indicators (data allowances, overage rates)
   - Voice plan indicators (minute allowances, call features)
   - SMS/messaging indicators (text allowances, messaging features)
   - Multi-service indicators (bundled service combinations)
2. Assign primary and secondary type classifications
3. Create type-indexed collections for efficient access

PHASE 2: DIVERSITY MAINTENANCE
1. Calculate type distribution requirements:
   - Minimum representation per type
   - Maximum concentration limits per type
   - Required coverage across all active types
2. Identify existing sequence gaps by type
3. Prioritize under-represented types for inclusion

PHASE 3: BALANCED GENERATION
1. Generate base sequences using standard permutation logic
2. Apply type balancing filters:
   - Reject sequences with excessive type concentration
   - Boost sequences with good type diversity
   - Ensure minimum type representation thresholds
3. Calculate type distribution scores for ranking

PHASE 4: TYPE-SPECIFIC OPTIMIZATION
1. Apply type-specific business rules:
   - Data plans: Prioritize by data allowance efficiency
   - Voice plans: Optimize for call pattern compatibility
   - SMS plans: Focus on messaging usage alignment
   - Bundle plans: Consider multi-service optimization potential
2. Adjust cost calculations for type-specific factors
3. Apply type-based constraints and preferences

OUTPUT: List<RatePlanSequence> - Type-balanced sequences optimized for diverse service coverage
```

### Type Classification Matrix

```
SERVICE TYPE     | CLASSIFICATION CRITERIA           | OPTIMIZATION FOCUS
-----------------|-----------------------------------|-------------------
Data Plans       | Data allowance > 0               | Usage efficiency
Voice Plans      | Minute allowance > 0             | Call pattern match
SMS Plans        | Message allowance > 0            | Text usage alignment
Bundle Plans     | Multiple service types           | Multi-service savings
IoT Plans        | Low data, machine-focused        | Device compatibility
Unlimited Plans  | No usage limits                  | Heavy usage scenarios
Prepaid Plans    | Pay-as-you-go structure         | Usage predictability
```

### Diversity Scoring Algorithm

```python
def calculate_diversity_score(sequence, type_distribution):
    """
    Calculate diversity score for a rate plan sequence
    """
    type_counts = count_types_in_sequence(sequence)
    total_plans = len(sequence)
    
    # Calculate entropy for type distribution
    entropy = 0
    for plan_type, count in type_counts.items():
        if count > 0:
            probability = count / total_plans
            entropy -= probability * log2(probability)
    
    # Normalize entropy score
    max_entropy = log2(len(type_distribution))
    diversity_score = entropy / max_entropy if max_entropy > 0 else 0
    
    # Apply penalties for extreme concentrations
    for plan_type, count in type_counts.items():
        concentration = count / total_plans
        if concentration > 0.7:  # More than 70% of one type
            diversity_score *= 0.5
    
    return diversity_score
```

## Code Location and Usage

### Primary Location
- **Function**: `GenerateRatePoolSequencesByRatePlanTypes()`
- **Class**: `RatePoolAssigner` (Static method)
- **Namespace**: `Altaworx.SimCard.Cost.Optimizer.Core`
- **Assembly**: External optimization core library

### Usage Locations in Codebase

#### 1. Mobility Carrier Optimization
**File**: `QueueCarrierPlanOptimization.cs`
**Line**: 660
```csharp
// Mobility optimization with type-specific sequence generation
var ratePoolSequences = RatePoolAssigner.GenerateRatePoolSequencesByRatePlanTypes(ratePoolCollection.RatePools);
```

#### 2. Type Filtering Context
**File**: `QueueCarrierPlanOptimization.cs`
**Line**: 708-714
```csharp
// Extract rate plan types for filtering
var ratePlanTypes = groupRatePlans.Select(x => x.RatePlanTypeId);
List<vwOptimizationSimCard> optimizationGroupSimCards = new List<vwOptimizationSimCard>();
foreach (var group in allOptimizationSimCards)
{
    if (group.Key == optimizationGroup.Id)
    {
        optimizationGroupSimCards = group.Where(x => ratePlanTypes.Contains(x.RatePlanTypeId)).ToList();
    }
}
```

#### 3. Optimization Configuration
**File**: `AltaworxSimCardCostOptimizer.cs`
**Line**: 255
```csharp
// Enable type filtering for Mobility portal
var shouldFilterByRatePlanType = instance.PortalType == PortalTypes.Mobility && !instance.IsCustomerOptimization;
```

#### 4. Device Assignment with Type Filtering
**File**: `QueueCarrierPlanOptimization.cs`
**Line**: 645
```csharp
// Apply type filtering during device assignment
var groupSimCardCount = BaseDeviceAssignment(context, instance.Id, sameRatePlansCollectionId, 
    serviceProviderId, null, null, new() { optimizationGroup.Name }, 
    ratePoolCollection, ratePools, optimizationGroupSimCards, billingPeriod, 
    usesProration, shouldFilterByRatePlanType: true);
```

## Integration Context

### Type Classification Workflow

1. **Pre-Classification Setup**:
   - Rate plans loaded with type metadata
   - Service characteristics analyzed
   - Type definitions validated

2. **Classification Process**:
   ```
   Rate Plan Loading → Type Analysis → Classification → Grouping → Sequence Generation
   ```

3. **Post-Classification Processing**:
   - Type-balanced sequences created
   - Diversity metrics calculated
   - Optimization queues generated with type distribution

### Business Rules by Type

#### Data Plan Rules
- **Primary Focus**: Data usage efficiency and overage cost minimization
- **Constraints**: Minimum data allowance requirements
- **Optimization**: Usage pattern matching and cost per MB analysis

#### Voice Plan Rules
- **Primary Focus**: Call minute optimization and feature compatibility
- **Constraints**: Geographic coverage requirements
- **Optimization**: Call pattern analysis and minute utilization efficiency

#### SMS/Messaging Plan Rules
- **Primary Focus**: Message volume optimization and delivery reliability
- **Constraints**: International messaging capabilities
- **Optimization**: Text usage patterns and cost per message analysis

#### Bundle Plan Rules
- **Primary Focus**: Multi-service cost optimization
- **Constraints**: Service combination requirements
- **Optimization**: Cross-service usage analysis and bundle efficiency

## Performance Considerations

### Type Classification Efficiency

- **Caching Strategy**: Type classifications cached to avoid repeated analysis
- **Parallel Processing**: Type analysis performed in parallel for large plan sets
- **Memory Optimization**: Type indices maintained for fast lookup operations

### Sequence Generation Optimization

- **Early Filtering**: Invalid type combinations eliminated before full sequence generation
- **Balanced Sampling**: Representative samples used for large type spaces
- **Adaptive Limits**: Sequence limits adjusted based on type diversity requirements

## Error Handling and Validation

### Type Classification Validation

- **Missing Type Data**: Default classification applied with warning logging
- **Invalid Type Combinations**: Sequences filtered out during generation
- **Type Constraint Violations**: Business rule validation with error reporting

### Diversity Maintenance Safeguards

- **Minimum Representation**: Ensures at least one sequence per active type
- **Maximum Concentration**: Prevents over-representation of any single type
- **Balance Verification**: Post-generation validation of type distribution

## Monitoring and Metrics

### Type Distribution Metrics

```csharp
// Example metrics for type classification monitoring
public class TypeClassificationMetrics
{
    public Dictionary<string, int> TypeCounts { get; set; }
    public double DiversityScore { get; set; }
    public double BalanceScore { get; set; }
    public int TotalSequences { get; set; }
    public List<string> UnrepresentedTypes { get; set; }
    public Dictionary<string, double> TypeConcentrations { get; set; }
}
```

### Performance Monitoring

- **Classification Time**: Time taken for type analysis and categorization
- **Sequence Generation Efficiency**: Sequences generated per unit time by type
- **Diversity Achievement**: Percentage of sequences meeting diversity criteria
- **Type Balance Success**: Ratio of balanced to unbalanced sequences

## Business Impact

### Optimization Benefits

- **Service Coverage**: Ensures comprehensive testing across all service types
- **Cost Efficiency**: Type-specific optimization maximizes savings per service category
- **Customer Satisfaction**: Balanced assignments prevent service gaps or over-provisioning
- **Strategic Planning**: Type distribution insights inform rate plan portfolio decisions

### Operational Excellence

- **Predictable Results**: Type balancing ensures consistent optimization quality
- **Scalable Processing**: Type-based organization enables efficient large-scale optimization
- **Maintainable Logic**: Clear type separation simplifies rule management and updates
- **Quality Assurance**: Type diversity requirements prevent optimization blind spots

---

*This analysis provides comprehensive documentation for the Rate Plan Type Classification operations within the Altaworx SIM Card Cost Optimization System architecture.*