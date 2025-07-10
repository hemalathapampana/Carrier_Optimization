# CROSS-PROVIDER VALIDATION RULES - ALGORITHMIC SPECIFICATION

## OVERVIEW
Cross-Provider optimization enables customers to optimize device costs across multiple carriers (AT&T, Verizon, T-Mobile, etc.) simultaneously, requiring comprehensive validation to ensure proper authorization, billing alignment, and service compatibility.

---

## ALGORITHM 1: CUSTOMER ID VALIDATION ACROSS ALL SUPPORTED PROVIDERS

### **WHAT**: Validates customer identity and access rights across multiple carrier providers
### **WHY**: Ensures customer has authorization to optimize devices across different carriers and prevents unauthorized access
### **HOW**: Cross-references customer credentials against all provider databases and validates service entitlements

### **LAMBDA**: AltaworxSimCardCostOptimizer.cs & AltaworxSimCardCostOptimizerCleanup.cs

```
ALGORITHM ValidateCustomerIdAcrossProviders
CONTEXT: Multi-provider customer validation during optimization initialization
CODE LOCATION: AltaworxSimCardCostOptimizer.cs, Lines 180-185, 335-340

INPUT:
    - instance: OptimizationInstance with customer information
    - serviceProviderIds: List<Integer> of all providers to validate against
    - customerType: {Rev, AMOP, CrossProvider}

OUTPUT:
    - validationResult: {isValid: Boolean, authorizedProviders: List<Integer>, errors: List<String>}

BEGIN
    LogInfo("Starting cross-provider customer validation")
    
    // STEP 1: EXTRACT CUSTOMER IDENTIFIERS
    amopCustomerId ← instance.AMOPCustomerId
    revCustomerId ← instance.RevCustomerId
    customerType ← instance.CustomerType
    tenantId ← instance.TenantId
    
    IF (amopCustomerId = NULL AND revCustomerId = NULL) THEN
        RETURN {isValid: FALSE, error: "No customer identifier provided"}
    END IF
    
    // STEP 2: VALIDATE CUSTOMER ACROSS ALL PROVIDERS
    authorizedProviders ← []
    validationErrors ← []
    
    FOR EACH serviceProviderId IN serviceProviderIds DO
        TRY
            // Validate customer access to specific provider
            IF amopCustomerId ≠ NULL THEN
                // AMOP Customer Validation
                amopCustomer ← GetAMOPCustomerById(context, amopCustomerId)
                IF amopCustomer = NULL THEN
                    validationErrors.ADD("AMOP Customer " + amopCustomerId + " not found")
                    CONTINUE
                END IF
                
                // Check customer's service provider associations
                customerProviderAccess ← ValidateAMOPCustomerProviderAccess(
                    context, amopCustomerId, serviceProviderId, tenantId)
                
                IF customerProviderAccess.hasAccess THEN
                    authorizedProviders.ADD(serviceProviderId)
                    LogInfo("AMOP Customer " + amopCustomerId + " authorized for provider " + serviceProviderId)
                ELSE
                    validationErrors.ADD("AMOP Customer " + amopCustomerId + " not authorized for provider " + serviceProviderId)
                END IF
                
            ELSE IF revCustomerId ≠ NULL THEN
                // Rev Customer Validation
                revCustomer ← GetRevCustomerById(context, revCustomerId)
                IF revCustomer = NULL THEN
                    validationErrors.ADD("Rev Customer " + revCustomerId + " not found")
                    CONTINUE
                END IF
                
                // Check customer's service provider associations
                customerProviderAccess ← ValidateRevCustomerProviderAccess(
                    context, revCustomerId, serviceProviderId, tenantId)
                
                IF customerProviderAccess.hasAccess THEN
                    authorizedProviders.ADD(serviceProviderId)
                    LogInfo("Rev Customer " + revCustomerId + " authorized for provider " + serviceProviderId)
                ELSE
                    validationErrors.ADD("Rev Customer " + revCustomerId + " not authorized for provider " + serviceProviderId)
                END IF
            END IF
            
        CATCH Exception e
            validationErrors.ADD("Provider " + serviceProviderId + " validation failed: " + e.message)
            LogError("Customer validation failed for provider " + serviceProviderId + ": " + e.message)
        END TRY
    END FOR
    
    // STEP 3: VALIDATE MINIMUM PROVIDER REQUIREMENTS
    IF authorizedProviders.COUNT < 2 THEN
        RETURN {
            isValid: FALSE, 
            error: "Cross-provider optimization requires access to at least 2 providers",
            authorizedProviders: authorizedProviders,
            validationErrors: validationErrors
        }
    END IF
    
    // STEP 4: VALIDATE CUSTOMER TYPE CONSISTENCY
    IF customerType = CrossProvider THEN
        // Additional cross-provider specific validations
        crossProviderValidation ← ValidateCrossProviderCustomerEligibility(
            context, amopCustomerId, revCustomerId, authorizedProviders)
        
        IF NOT crossProviderValidation.isEligible THEN
            RETURN {
                isValid: FALSE,
                error: "Customer not eligible for cross-provider optimization: " + crossProviderValidation.reason,
                authorizedProviders: authorizedProviders
            }
        END IF
    END IF
    
    LogInfo("Customer validation successful. Authorized providers: " + authorizedProviders.COUNT)
    RETURN {
        isValid: TRUE,
        authorizedProviders: authorizedProviders,
        validationErrors: validationErrors
    }
END
```

---

## ALGORITHM 2: CROSS-PROVIDER BILLING PERIOD ALIGNMENT

### **WHAT**: Ensures billing periods are synchronized across all providers for accurate cost optimization
### **WHY**: Prevents optimization errors from misaligned billing cycles and ensures fair cost comparisons
### **HOW**: Validates billing period consistency and handles provider-specific billing cycle variations

### **LAMBDA**: AltaworxSimCardCostOptimizerCleanup.cs

```
ALGORITHM ValidateCrossProviderBillingAlignment
CONTEXT: Multi-provider billing period synchronization validation
CODE LOCATION: AltaworxSimCardCostOptimizerCleanup.cs, Lines 2280, 1189

INPUT:
    - instance: OptimizationInstance with billing information
    - serviceProviderIds: List<Integer> of all providers
    - billingTimeZone: TimeZoneInfo for consistent time calculations

OUTPUT:
    - alignmentResult: {isAligned: Boolean, billingPeriod: BillingPeriod, providerVariations: List<ProviderBillingVariation>}

BEGIN
    LogInfo("Validating cross-provider billing period alignment")
    
    // STEP 1: GET PRIMARY BILLING PERIOD
    primaryBillingPeriod ← NULL
    IF instance.CustomerBillingPeriodId ≠ NULL THEN
        // Customer-specific billing period for cross-provider
        primaryBillingPeriod ← crossProviderOptimizationRepository.GetBillingPeriod(
            context, instance.AMOPCustomerId, instance.CustomerBillingPeriodId, billingTimeZone)
    ELSE
        // Standard billing period
        primaryBillingPeriod ← NEW BillingPeriod(
            instance.BillingPeriodIdByPortalType,
            instance.ServiceProviderId,
            instance.BillingPeriodEndDate.Year,
            instance.BillingPeriodEndDate.Month,
            instance.BillingPeriodEndDate.Day,
            instance.BillingPeriodEndDate.Hour,
            billingTimeZone,
            instance.BillingPeriodEndDate)
    END IF
    
    IF primaryBillingPeriod = NULL THEN
        RETURN {isAligned: FALSE, error: "Primary billing period not found"}
    END IF
    
    // STEP 2: VALIDATE BILLING PERIOD FOR EACH PROVIDER
    providerVariations ← []
    alignmentTolerance ← TimeSpan.FromHours(24) // 24-hour tolerance for provider variations
    
    FOR EACH serviceProviderId IN serviceProviderIds DO
        TRY
            // Get provider-specific billing period
            providerBillingPeriod ← GetProviderBillingPeriod(context, serviceProviderId, primaryBillingPeriod.Year, primaryBillingPeriod.Month)
            
            // Calculate alignment differences
            startDateDifference ← ABS(providerBillingPeriod.BillingPeriodStart - primaryBillingPeriod.BillingPeriodStart)
            endDateDifference ← ABS(providerBillingPeriod.BillingPeriodEnd - primaryBillingPeriod.BillingPeriodEnd)
            
            // Check if within acceptable tolerance
            IF startDateDifference > alignmentTolerance OR endDateDifference > alignmentTolerance THEN
                variation ← NEW ProviderBillingVariation{
                    serviceProviderId: serviceProviderId,
                    expectedStart: primaryBillingPeriod.BillingPeriodStart,
                    actualStart: providerBillingPeriod.BillingPeriodStart,
                    expectedEnd: primaryBillingPeriod.BillingPeriodEnd,
                    actualEnd: providerBillingPeriod.BillingPeriodEnd,
                    startDifference: startDateDifference,
                    endDifference: endDateDifference,
                    isWithinTolerance: FALSE
                }
                providerVariations.ADD(variation)
                LogWarning("Billing period misalignment for provider " + serviceProviderId + 
                          ". Start difference: " + startDateDifference.TotalHours + " hours, " +
                          "End difference: " + endDateDifference.TotalHours + " hours")
            ELSE
                LogInfo("Billing period aligned for provider " + serviceProviderId)
            END IF
            
        CATCH Exception e
            LogError("Failed to validate billing period for provider " + serviceProviderId + ": " + e.message)
            variation ← NEW ProviderBillingVariation{
                serviceProviderId: serviceProviderId,
                error: e.message,
                isWithinTolerance: FALSE
            }
            providerVariations.ADD(variation)
        END TRY
    END FOR
    
    // STEP 3: DETERMINE OVERALL ALIGNMENT STATUS
    criticalMisalignments ← providerVariations.WHERE(v → NOT v.isWithinTolerance).COUNT
    
    IF criticalMisalignments > 0 THEN
        LogError("Billing period alignment failed. " + criticalMisalignments + " providers have critical misalignments")
        RETURN {
            isAligned: FALSE,
            billingPeriod: primaryBillingPeriod,
            providerVariations: providerVariations,
            error: "Billing periods not aligned across providers"
        }
    END IF
    
    // STEP 4: CALCULATE UNIFIED BILLING PERIOD
    // Use the most restrictive (latest start, earliest end) billing period
    unifiedStartDate ← primaryBillingPeriod.BillingPeriodStart
    unifiedEndDate ← primaryBillingPeriod.BillingPeriodEnd
    
    FOR EACH variation IN providerVariations DO
        IF variation.actualStart > unifiedStartDate THEN
            unifiedStartDate ← variation.actualStart
        END IF
        IF variation.actualEnd < unifiedEndDate THEN
            unifiedEndDate ← variation.actualEnd
        END IF
    END FOR
    
    unifiedBillingPeriod ← NEW BillingPeriod{
        BillingPeriodStart: unifiedStartDate,
        BillingPeriodEnd: unifiedEndDate,
        BillingTimeZone: billingTimeZone
    }
    
    LogInfo("Billing period alignment successful. Unified period: " + 
            unifiedStartDate.ToString() + " to " + unifiedEndDate.ToString())
    
    RETURN {
        isAligned: TRUE,
        billingPeriod: unifiedBillingPeriod,
        providerVariations: providerVariations
    }
END
```

---

## ALGORITHM 3: INTEGRATION AUTHENTICATION VALIDATION

### **WHAT**: Verifies API credentials and authentication for each carrier provider
### **WHY**: Ensures secure access to carrier APIs and prevents authentication failures during optimization
### **HOW**: Tests API connectivity and validates authentication tokens for all providers

### **LAMBDA**: QueueCarrierPlanOptimization.cs

```
ALGORITHM ValidateIntegrationAuthentication
CONTEXT: Multi-provider API authentication validation during queue setup
CODE LOCATION: QueueCarrierPlanOptimization.cs, Lines 324, 328, 462

INPUT:
    - serviceProviderIds: List<Integer> of all providers requiring authentication
    - tenantId: Integer for tenant-specific credentials
    - billingPeriod: BillingPeriod for context-specific validation

OUTPUT:
    - authenticationResult: {isValid: Boolean, validProviders: List<Integer>, authenticationDetails: List<AuthenticationDetail>}

BEGIN
    LogInfo("Starting cross-provider integration authentication validation")
    
    validProviders ← []
    authenticationDetails ← []
    
    // STEP 1: VALIDATE AUTHENTICATION FOR EACH PROVIDER
    FOR EACH serviceProviderId IN serviceProviderIds DO
        TRY
            LogInfo("Validating authentication for provider " + serviceProviderId)
            
            // Get integration authentication ID for provider
            integrationAuthenticationId ← serviceProviderRepository.GetIntegrationAuthenticationId(serviceProviderId)
            
            IF integrationAuthenticationId = NULL THEN
                authDetail ← NEW AuthenticationDetail{
                    serviceProviderId: serviceProviderId,
                    isValid: FALSE,
                    error: "No integration authentication configured",
                    authenticationId: NULL
                }
                authenticationDetails.ADD(authDetail)
                LogError("No integration authentication found for provider " + serviceProviderId)
                CONTINUE
            END IF
            
            // STEP 2: TEST API CONNECTIVITY
            connectivityTest ← TestProviderAPIConnectivity(serviceProviderId, integrationAuthenticationId, tenantId)
            
            IF NOT connectivityTest.isSuccessful THEN
                authDetail ← NEW AuthenticationDetail{
                    serviceProviderId: serviceProviderId,
                    isValid: FALSE,
                    error: "API connectivity test failed: " + connectivityTest.errorMessage,
                    authenticationId: integrationAuthenticationId
                }
                authenticationDetails.ADD(authDetail)
                LogError("API connectivity failed for provider " + serviceProviderId + ": " + connectivityTest.errorMessage)
                CONTINUE
            END IF
            
            // STEP 3: VALIDATE CREDENTIALS AND PERMISSIONS
            credentialValidation ← ValidateProviderCredentials(serviceProviderId, integrationAuthenticationId, tenantId)
            
            IF NOT credentialValidation.isValid THEN
                authDetail ← NEW AuthenticationDetail{
                    serviceProviderId: serviceProviderId,
                    isValid: FALSE,
                    error: "Credential validation failed: " + credentialValidation.errorMessage,
                    authenticationId: integrationAuthenticationId
                }
                authenticationDetails.ADD(authDetail)
                LogError("Credential validation failed for provider " + serviceProviderId + ": " + credentialValidation.errorMessage)
                CONTINUE
            END IF
            
            // STEP 4: TEST OPTIMIZATION-SPECIFIC PERMISSIONS
            optimizationPermissions ← ValidateOptimizationPermissions(serviceProviderId, integrationAuthenticationId, tenantId)
            
            IF NOT optimizationPermissions.hasPermission THEN
                authDetail ← NEW AuthenticationDetail{
                    serviceProviderId: serviceProviderId,
                    isValid: FALSE,
                    error: "Insufficient permissions for optimization: " + optimizationPermissions.missingPermissions,
                    authenticationId: integrationAuthenticationId
                }
                authenticationDetails.ADD(authDetail)
                LogError("Insufficient optimization permissions for provider " + serviceProviderId)
                CONTINUE
            END IF
            
            // STEP 5: VALIDATE SIM CARD COUNT EXPECTATIONS
            expectedSimCount ← GetExpectedOptimizationSimCardCount(
                context, serviceProviderId, NULL, billingPeriod.BillingPeriodId, 
                integrationAuthenticationId, tenantId)
            
            IF expectedSimCount ≤ 0 THEN
                authDetail ← NEW AuthenticationDetail{
                    serviceProviderId: serviceProviderId,
                    isValid: FALSE,
                    error: "No devices found for optimization - authentication may be invalid",
                    authenticationId: integrationAuthenticationId,
                    expectedSimCount: 0
                }
                authenticationDetails.ADD(authDetail)
                LogWarning("No devices found for provider " + serviceProviderId + " - authentication may be invalid")
                CONTINUE
            END IF
            
            // Authentication successful
            validProviders.ADD(serviceProviderId)
            authDetail ← NEW AuthenticationDetail{
                serviceProviderId: serviceProviderId,
                isValid: TRUE,
                authenticationId: integrationAuthenticationId,
                expectedSimCount: expectedSimCount,
                lastValidated: GetCurrentTimestamp()
            }
            authenticationDetails.ADD(authDetail)
            LogInfo("Authentication validated successfully for provider " + serviceProviderId + 
                   ". Expected SIM count: " + expectedSimCount)
            
        CATCH Exception e
            authDetail ← NEW AuthenticationDetail{
                serviceProviderId: serviceProviderId,
                isValid: FALSE,
                error: "Authentication validation exception: " + e.message,
                authenticationId: integrationAuthenticationId
            }
            authenticationDetails.ADD(authDetail)
            LogError("Authentication validation failed for provider " + serviceProviderId + ": " + e.message)
        END TRY
    END FOR
    
    // STEP 6: VALIDATE MINIMUM PROVIDER REQUIREMENTS
    IF validProviders.COUNT < 2 THEN
        LogError("Cross-provider optimization requires at least 2 valid provider authentications. Found: " + validProviders.COUNT)
        RETURN {
            isValid: FALSE,
            validProviders: validProviders,
            authenticationDetails: authenticationDetails,
            error: "Insufficient valid provider authentications for cross-provider optimization"
        }
    END IF
    
    LogInfo("Integration authentication validation successful for " + validProviders.COUNT + " providers")
    RETURN {
        isValid: TRUE,
        validProviders: validProviders,
        authenticationDetails: authenticationDetails
    }
END
```

---

## ALGORITHM 4: SERVICE PROVIDER ASSOCIATIONS AND CAPABILITIES

### **WHAT**: Validates provider service associations and optimization capabilities for cross-provider scenarios
### **WHY**: Ensures all providers support required optimization features and are properly associated with customer accounts
### **HOW**: Checks provider capabilities, service offerings, and customer-provider relationships

### **LAMBDA**: AltaworxSimCardCostOptimizer.cs & AltaworxSimCardCostOptimizerCleanup.cs

```
ALGORITHM ValidateServiceProviderAssociationsAndCapabilities
CONTEXT: Cross-provider service capability and association validation
CODE LOCATION: AltaworxSimCardCostOptimizer.cs, Lines 296-298; AltaworxSimCardCostOptimizerCleanup.cs, Lines 2333-2345

INPUT:
    - instance: OptimizationInstance with provider information
    - serviceProviderIds: List<Integer> of providers to validate
    - portalType: PortalTypes.CrossProvider

OUTPUT:
    - associationResult: {isValid: Boolean, validAssociations: List<ProviderAssociation>, capabilityDetails: List<ProviderCapability>}

BEGIN
    LogInfo("Validating service provider associations and capabilities for cross-provider optimization")
    
    validAssociations ← []
    capabilityDetails ← []
    
    // STEP 1: VALIDATE PORTAL TYPE COMPATIBILITY
    IF portalType ≠ PortalTypes.CrossProvider THEN
        RETURN {
            isValid: FALSE,
            error: "Portal type must be CrossProvider for cross-provider validation"
        }
    END IF
    
    // STEP 2: VALIDATE EACH PROVIDER'S ASSOCIATIONS AND CAPABILITIES
    FOR EACH serviceProviderId IN serviceProviderIds DO
        TRY
            LogInfo("Validating provider " + serviceProviderId + " associations and capabilities")
            
            // Validate provider-customer association
            providerAssociation ← ValidateProviderCustomerAssociation(
                context, serviceProviderId, instance.AMOPCustomerId, instance.RevCustomerId, instance.TenantId)
            
            IF NOT providerAssociation.isAssociated THEN
                LogError("Provider " + serviceProviderId + " not associated with customer")
                CONTINUE
            END IF
            
            // Validate cross-provider optimization capability
            crossProviderCapability ← ValidateCrossProviderOptimizationCapability(context, serviceProviderId)
            
            capability ← NEW ProviderCapability{
                serviceProviderId: serviceProviderId,
                supportsCrossProviderOptimization: crossProviderCapability.isSupported,
                supportsRatePlanOptimization: crossProviderCapability.supportsRatePlanOptimization,
                supportsUsageBasedOptimization: crossProviderCapability.supportsUsageBasedOptimization,
                supportsSharedPooling: crossProviderCapability.supportsSharedPooling,
                maxDevicesSupported: crossProviderCapability.maxDevicesSupported,
                supportedOptimizationStrategies: crossProviderCapability.supportedStrategies
            }
            
            IF NOT capability.supportsCrossProviderOptimization THEN
                capability.validationError ← "Provider does not support cross-provider optimization"
                capabilityDetails.ADD(capability)
                LogError("Provider " + serviceProviderId + " does not support cross-provider optimization")
                CONTINUE
            END IF
            
            // Validate rate plan compatibility
            ratePoolCompatibility ← ValidateRatePoolCompatibility(context, serviceProviderId, instance)
            
            IF NOT ratePoolCompatibility.isCompatible THEN
                capability.validationError ← "Rate pool compatibility issue: " + ratePoolCompatibility.errorMessage
                capabilityDetails.ADD(capability)
                LogError("Rate pool compatibility failed for provider " + serviceProviderId + ": " + ratePoolCompatibility.errorMessage)
                CONTINUE
            END IF
            
            // Validate device access and permissions
            deviceAccess ← ValidateDeviceAccessPermissions(context, serviceProviderId, instance)
            
            IF NOT deviceAccess.hasAccess THEN
                capability.validationError ← "Device access denied: " + deviceAccess.errorMessage
                capabilityDetails.ADD(capability)
                LogError("Device access validation failed for provider " + serviceProviderId + ": " + deviceAccess.errorMessage)
                CONTINUE
            END IF
            
            // Get device count for capability assessment
            deviceCount ← GetCrossProviderDeviceCount(context, serviceProviderId, instance)
            capability.currentDeviceCount ← deviceCount
            
            IF deviceCount > capability.maxDevicesSupported THEN
                capability.validationError ← "Device count (" + deviceCount + ") exceeds provider limit (" + capability.maxDevicesSupported + ")"
                capabilityDetails.ADD(capability)
                LogError("Device count exceeds provider capability for provider " + serviceProviderId)
                CONTINUE
            END IF
            
            // Successful validation
            validAssociations.ADD(providerAssociation)
            capabilityDetails.ADD(capability)
            LogInfo("Provider " + serviceProviderId + " validation successful. Device count: " + deviceCount)
            
        CATCH Exception e
            capability ← NEW ProviderCapability{
                serviceProviderId: serviceProviderId,
                validationError: "Validation exception: " + e.message
            }
            capabilityDetails.ADD(capability)
            LogError("Provider validation failed for " + serviceProviderId + ": " + e.message)
        END TRY
    END FOR
    
    // STEP 3: VALIDATE CROSS-PROVIDER COMPATIBILITY
    IF validAssociations.COUNT < 2 THEN
        RETURN {
            isValid: FALSE,
            validAssociations: validAssociations,
            capabilityDetails: capabilityDetails,
            error: "Insufficient valid provider associations for cross-provider optimization"
        }
    END IF
    
    // STEP 4: VALIDATE INTER-PROVIDER COMPATIBILITY
    compatibilityMatrix ← ValidateInterProviderCompatibility(validAssociations, capabilityDetails)
    
    IF NOT compatibilityMatrix.isCompatible THEN
        RETURN {
            isValid: FALSE,
            validAssociations: validAssociations,
            capabilityDetails: capabilityDetails,
            error: "Provider capabilities are not compatible for cross-provider optimization: " + compatibilityMatrix.errorMessage
        }
    END IF
    
    LogInfo("Service provider associations and capabilities validation successful for " + validAssociations.COUNT + " providers")
    RETURN {
        isValid: TRUE,
        validAssociations: validAssociations,
        capabilityDetails: capabilityDetails
    }
END
```

---

## ALGORITHM 5: CROSS-PROVIDER ELIGIBILITY AND RESTRICTIONS

### **WHAT**: Validates customer eligibility and enforces business rules for cross-provider optimization
### **WHY**: Ensures compliance with carrier agreements and business policies for multi-provider scenarios
### **HOW**: Checks eligibility criteria, validates restrictions, and enforces policy compliance

### **LAMBDA**: AltaworxSimCardCostOptimizerCleanup.cs

```
ALGORITHM ValidateCrossProviderEligibilityAndRestrictions
CONTEXT: Cross-provider eligibility and business rule validation
CODE LOCATION: AltaworxSimCardCostOptimizerCleanup.cs, Lines 2350, 216, 364

INPUT:
    - instance: OptimizationInstance with customer and provider information
    - serviceProviderIds: List<Integer> of providers for cross-provider optimization
    - isCustomerOptimization: Boolean indicating customer-specific optimization

OUTPUT:
    - eligibilityResult: {isEligible: Boolean, restrictions: List<EligibilityRestriction>, allowedOperations: List<String>}

BEGIN
    LogInfo("Validating cross-provider eligibility and restrictions")
    
    restrictions ← []
    allowedOperations ← []
    
    // STEP 1: VALIDATE CUSTOMER TYPE ELIGIBILITY
    customerType ← instance.CustomerType
    
    IF customerType ≠ SiteTypes.AMOP AND NOT isCustomerOptimization THEN
        restriction ← NEW EligibilityRestriction{
            restrictionType: "CUSTOMER_TYPE",
            description: "Cross-provider optimization requires AMOP customer type or customer-specific optimization",
            isBlocking: TRUE
        }
        restrictions.ADD(restriction)
    END IF
    
    // STEP 2: VALIDATE PORTAL TYPE COMPATIBILITY
    IF instance.PortalType ≠ PortalTypes.CrossProvider THEN
        restriction ← NEW EligibilityRestriction{
            restrictionType: "PORTAL_TYPE",
            description: "Portal type must be set to CrossProvider for cross-provider optimization",
            isBlocking: TRUE
        }
        restrictions.ADD(restriction)
    END IF
    
    // STEP 3: VALIDATE SERVICE PROVIDER COUNT
    IF serviceProviderIds.COUNT < 2 THEN
        restriction ← NEW EligibilityRestriction{
            restrictionType: "PROVIDER_COUNT",
            description: "Cross-provider optimization requires at least 2 service providers",
            isBlocking: TRUE
        }
        restrictions.ADD(restriction)
    END IF
    
    // STEP 4: VALIDATE CUSTOMER-SPECIFIC RESTRICTIONS
    IF isCustomerOptimization THEN
        // Customer optimization specific validations
        customerRestrictions ← ValidateCustomerOptimizationEligibility(
            context, instance.AMOPCustomerId, instance.RevCustomerId, serviceProviderIds)
        
        FOR EACH customerRestriction IN customerRestrictions DO
            restrictions.ADD(customerRestriction)
        END FOR
        
        // Add customer optimization allowed operations
        allowedOperations.ADD("CUSTOMER_SPECIFIC_OPTIMIZATION")
        allowedOperations.ADD("SHARED_POOL_OPTIMIZATION")
        allowedOperations.ADD("CROSS_PROVIDER_RATE_POOL_GENERATION")
    END IF
    
    // STEP 5: VALIDATE BILLING PERIOD RESTRICTIONS
    currentTime ← GetCurrentTimestamp()
    billingPeriodEnd ← instance.BillingPeriodEndDate
    timeUntilBillingEnd ← billingPeriodEnd - currentTime
    
    IF timeUntilBillingEnd.TotalHours < 24 THEN
        restriction ← NEW EligibilityRestriction{
            restrictionType: "BILLING_PERIOD_TIMING",
            description: "Cross-provider optimization cannot be performed within 24 hours of billing period end",
            isBlocking: TRUE
        }
        restrictions.ADD(restriction)
    ELSE IF timeUntilBillingEnd.TotalHours < 48 THEN
        restriction ← NEW EligibilityRestriction{
            restrictionType: "BILLING_PERIOD_TIMING",
            description: "Cross-provider optimization within 48 hours of billing end - automatic rate plan updates disabled",
            isBlocking: FALSE
        }
        restrictions.ADD(restriction)
    ELSE
        allowedOperations.ADD("AUTOMATIC_RATE_PLAN_UPDATES")
    END IF
    
    // STEP 6: VALIDATE PROVIDER-SPECIFIC RESTRICTIONS
    FOR EACH serviceProviderId IN serviceProviderIds DO
        providerRestrictions ← ValidateProviderSpecificRestrictions(
            context, serviceProviderId, instance, isCustomerOptimization)
        
        FOR EACH providerRestriction IN providerRestrictions DO
            restrictions.ADD(providerRestriction)
        END FOR
    END FOR
    
    // STEP 7: VALIDATE INTER-PROVIDER AGREEMENT COMPLIANCE
    interProviderCompliance ← ValidateInterProviderAgreementCompliance(
        context, serviceProviderIds, instance.TenantId)
    
    IF NOT interProviderCompliance.isCompliant THEN
        restriction ← NEW EligibilityRestriction{
            restrictionType: "INTER_PROVIDER_AGREEMENT",
            description: "Inter-provider agreement compliance failed: " + interProviderCompliance.violationDetails,
            isBlocking: TRUE
        }
        restrictions.ADD(restriction)
    END IF
    
    // STEP 8: VALIDATE DEVICE COUNT LIMITS
    totalDeviceCount ← 0
    FOR EACH serviceProviderId IN serviceProviderIds DO
        providerDeviceCount ← GetProviderDeviceCount(context, serviceProviderId, instance)
        totalDeviceCount ← totalDeviceCount + providerDeviceCount
    END FOR
    
    maxCrossProviderDeviceLimit ← GetMaxCrossProviderDeviceLimit(context, instance.TenantId)
    
    IF totalDeviceCount > maxCrossProviderDeviceLimit THEN
        restriction ← NEW EligibilityRestriction{
            restrictionType: "DEVICE_COUNT_LIMIT",
            description: "Total device count (" + totalDeviceCount + ") exceeds cross-provider limit (" + maxCrossProviderDeviceLimit + ")",
            isBlocking: TRUE
        }
        restrictions.ADD(restriction)
    END IF
    
    // STEP 9: VALIDATE OPTIMIZATION COMPLEXITY LIMITS
    optimizationComplexity ← CalculateOptimizationComplexity(serviceProviderIds, totalDeviceCount, instance)
    maxComplexity ← GetMaxOptimizationComplexity(context)
    
    IF optimizationComplexity > maxComplexity THEN
        restriction ← NEW EligibilityRestriction{
            restrictionType: "OPTIMIZATION_COMPLEXITY",
            description: "Optimization complexity (" + optimizationComplexity + ") exceeds maximum allowed (" + maxComplexity + ")",
            isBlocking: TRUE
        }
        restrictions.ADD(restriction)
    END IF
    
    // STEP 10: DETERMINE OVERALL ELIGIBILITY
    blockingRestrictions ← restrictions.WHERE(r → r.isBlocking).COUNT
    
    IF blockingRestrictions > 0 THEN
        LogError("Cross-provider eligibility validation failed. Blocking restrictions: " + blockingRestrictions)
        RETURN {
            isEligible: FALSE,
            restrictions: restrictions,
            allowedOperations: allowedOperations,
            error: "Customer not eligible for cross-provider optimization due to " + blockingRestrictions + " blocking restrictions"
        }
    END IF
    
    // Add standard allowed operations
    allowedOperations.ADD("CROSS_PROVIDER_OPTIMIZATION")
    allowedOperations.ADD("MULTI_PROVIDER_COST_COMPARISON")
    allowedOperations.ADD("PROVIDER_MIGRATION_ANALYSIS")
    
    LogInfo("Cross-provider eligibility validation successful. Total restrictions: " + restrictions.COUNT + 
            " (non-blocking). Allowed operations: " + allowedOperations.COUNT)
    
    RETURN {
        isEligible: TRUE,
        restrictions: restrictions,
        allowedOperations: allowedOperations
    }
END
```

---

## SUPPORTING DATA STRUCTURES

```
TYPE ProviderAssociation = RECORD
    serviceProviderId: Integer
    isAssociated: Boolean
    associationType: String
    permissions: List<String>
    lastValidated: DateTime
END TYPE

TYPE ProviderCapability = RECORD
    serviceProviderId: Integer
    supportsCrossProviderOptimization: Boolean
    supportsRatePlanOptimization: Boolean
    supportsUsageBasedOptimization: Boolean
    supportsSharedPooling: Boolean
    maxDevicesSupported: Integer
    currentDeviceCount: Integer
    supportedOptimizationStrategies: List<String>
    validationError: String
END TYPE

TYPE EligibilityRestriction = RECORD
    restrictionType: String
    description: String
    isBlocking: Boolean
    affectedProviders: List<Integer>
    remediation: String
END TYPE

TYPE AuthenticationDetail = RECORD
    serviceProviderId: Integer
    isValid: Boolean
    authenticationId: Integer
    expectedSimCount: Integer
    error: String
    lastValidated: DateTime
END TYPE
```

---

## EXECUTION FLOW SUMMARY

```
Cross-Provider Validation Flow:
1. QueueCarrierPlanOptimization → Integration Authentication Validation
2. AltaworxSimCardCostOptimizer → Customer ID & Provider Association Validation
3. AltaworxSimCardCostOptimizer → Billing Period Alignment Validation  
4. AltaworxSimCardCostOptimizerCleanup → Eligibility & Restrictions Validation
5. All Lambdas → Continuous monitoring and validation throughout process
```

Each algorithm ensures that cross-provider optimization operates securely, efficiently, and in compliance with all business rules and carrier agreements while providing detailed validation feedback for troubleshooting and monitoring purposes.