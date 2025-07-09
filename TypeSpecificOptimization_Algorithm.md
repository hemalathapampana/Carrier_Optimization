# Type-Specific Optimization Algorithm

## Overview

This document describes the Type-Specific Optimization algorithm that applies specialized business rules and cost calculations specific to each rate plan type within the Altaworx SIM Card Cost Optimization System.

## Algorithm Description

### TYPE-SPECIFIC OPTIMIZATION ALGORITHM

#### INPUT: 
- Portal type classification (M2M, Mobility, or CrossProvider)
- Rate plan collection with service type identifiers
- Device group collection filtered by service types
- Type filtering enablement flag

#### ALGORITHM:

##### 1. TYPE_FILTER_INITIALIZATION:
Determine if type filtering should be enabled based on portal configuration:
- If the portal is Mobility type and not a customer-specific optimization:
  - Enable rate plan type filtering
  - Enable usage pooling between rate plans if collection supports pooling
- Otherwise, use standard optimization without type filtering

##### 2. RATE_PLAN_TYPE_EXTRACTION:
Extract all unique rate plan type identifiers from the grouped rate plans
Create a collection of these type identifiers for filtering purposes

##### 3. DEVICE_FILTERING_BY_TYPE:
Filter the complete device collection to include only devices that match
the rate plan types identified in the previous step
This ensures devices are aligned with available rate plan service types

##### 4. TYPE_SPECIFIC_DEVICE_ASSIGNMENT:
Execute device assignment using type-specific parameters:
- Use the filtered device collection
- Apply the optimization group configuration
- Include rate pool collection and individual rate pools
- Consider billing period and proration settings
- Enable type filtering flag to apply service-specific rules

##### 5. TYPE_SPECIFIC_SEQUENCE_GENERATION:
Generate rate plan sequences based on type filtering configuration:
- **If type filtering is enabled:**
  - Generate sequences using type-aware permutation algorithms
  - Apply diversity rules to ensure balanced type representation
- **If type filtering is disabled:**
  - Generate sequences using standard permutation algorithms
  - Apply general optimization rules without type constraints

##### 6. PORTAL_TYPE_SPECIFIC_PROCESSING:
Apply specialized processing based on the portal type:
- **M2M Portal:** Execute standard rate plan optimization without type restrictions
- **Mobility Portal:** Enable type filtering capabilities and usage pooling features
- **CrossProvider Portal:** Apply cross-carrier optimization logic with type awareness

##### 7. TYPE_SPECIFIC_VALIDATION:
Validate optimization results for type consistency:
- Check if any device results have undefined rate plan types or optimization groups
- Remove any results with missing type classifications
- Ensure all remaining results have valid type assignments and group associations
- Maintain only results that meet type-specific quality criteria

#### OUTPUT: 
- Device assignments optimized for specific service types
- Rate plan sequences balanced across service type categories
- Portal-specific optimization results with validated type compliance

## Implementation Details

### Portal Type Processing

#### M2M Portal Characteristics:
- Standard rate plan optimization approach
- No type filtering restrictions applied
- Focus on machine-to-machine device optimization
- Traditional cost-based assignment algorithms

#### Mobility Portal Characteristics:
- Type filtering enabled for service-specific optimization
- Usage pooling capabilities activated
- Mobile device-specific business rules applied
- Enhanced type diversity maintenance

#### CrossProvider Portal Characteristics:
- Multi-carrier optimization logic
- Type-aware cross-provider comparisons
- Advanced constraint handling for provider switching
- Comprehensive type validation across carriers

### Type Filtering Benefits

1. **Service Alignment**: Ensures devices are matched with compatible rate plan types
2. **Optimization Accuracy**: Applies service-specific cost calculations and constraints
3. **Business Rule Compliance**: Enforces type-specific business rules and requirements
4. **Quality Assurance**: Validates type consistency throughout the optimization process

### Validation Criteria

- **Type Classification Completeness**: All results must have defined rate plan types
- **Optimization Group Association**: All results must belong to valid optimization groups
- **Service Type Compatibility**: Device assignments must match compatible service types
- **Business Rule Compliance**: All assignments must meet type-specific business requirements

---

*This algorithm ensures that optimization results are tailored to specific service types while maintaining compatibility and compliance with business rules and technical constraints.*