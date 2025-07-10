# M2M Reports: English Algorithmic Analysis

## Overview

M2M (Machine-to-Machine) Reports are generated within the AWS Lambda function **AltaworxSimCardCostOptimizerCleanup.cs**, specifically using the `WriteM2MResults()` method located at **lines 746-850**. These reports provide comprehensive analysis of IoT device optimization results for carriers and customers.

---

## 1. Device Assignment Spreadsheets

### What This Report Does
Device Assignment Spreadsheets create detailed Excel files showing exactly which SIM cards and IoT devices are assigned to specific rate plans. The spreadsheet compares original assignments with the new optimized assignments, providing a clear before-and-after view of device allocation.

### Why This Report is Important
- **Transparency**: Customers can see exactly which devices moved to which rate plans and understand the optimization decisions
- **Validation**: Technical teams can verify that optimization logic worked correctly by reviewing individual device assignments
- **Implementation Guide**: Operations teams use this data to actually implement the rate plan changes in their systems
- **Audit Documentation**: Creates a permanent record of optimization decisions for compliance and historical reference

### How the Report Generation Works
The system collects device results from completed optimization queues, maps each device to its assigned rate pool, and exports everything into a structured Excel spreadsheet format.

### Code Location and Implementation
**Primary Method**: `AltaworxSimCardCostOptimizerCleanup.cs:796-798`

### English Algorithm for Device Assignment Generation

**Step 1: Set Up the Assignment Collection**
The system first initializes an empty assignment file container and creates a new M2M optimization result object to hold all the device assignment data.

**Step 2: Process Each Optimization Queue**
For every queue ID that contains optimization results:
- The system retrieves all device results from that specific queue using the GetM2MResults method
- It then builds the optimization result by adding these devices to the appropriate rate pools
- This process repeats for each queue until all optimization results are collected

**Step 3: Generate the Assignment File**
Once all device data is collected, the system uses the RatePoolAssignmentWriter to create a structured assignment file containing all device-to-rate-plan mappings.

**Step 4: Create the Final Excel Spreadsheet**
The system combines the assignment file with statistical data and any shared pool information to generate a comprehensive Excel file using the GenerateExcelFileFromByteArrays method.

### Data Retrieval Process
**Location**: `AltaworxSimCardCostOptimizerCleanup.cs:833-893`

**Step 1: Input Validation**
The system first checks if there are any queue IDs to process. If the queue list is empty, it logs a warning message and returns an empty device list.

**Step 2: Database Query Execution**
The system executes a complex SQL query that joins multiple tables:
- It pulls device information from the main Device table
- Retrieves usage data in megabytes for each device
- Gets communication plan details from JasperCommunicationPlan
- Fetches rate plan codes from either carrier or customer rate plan tables
- Collects rate pool assignments and names
- Gathers all cost information including base rates, rate charges, and overage amounts

**Step 3: Process the Query Results**
As the system reads each row from the database:
- It creates a new SimCardResult object for each device
- Populates all the device details, usage information, and cost data
- Adds each completed SimCardResult to the growing list of devices

**Step 4: Calculate Billing Period Information**
For each device in the final list:
- The system determines if the device was activated during the current billing period
- It calculates how many days the device was active within the billing period
- This information is crucial for accurate cost calculations and proration

---

## 2. Cost Savings Summaries

### What This Report Does
Cost Savings Summaries calculate and present the financial impact of the optimization process. The report shows original costs, optimized costs, and the total savings achieved, providing clear evidence of the optimization's financial value.

### Why This Report is Important
- **ROI Demonstration**: Proves the financial value and return on investment of the optimization process to stakeholders
- **Financial Reporting**: Provides accurate data for financial analysis, budgeting, and cost accounting purposes
- **Decision Making Support**: Helps management evaluate whether optimization strategies are effective and worth continuing
- **Stakeholder Communication**: Offers clear, quantifiable savings metrics that can be easily understood by executives and customers

### How the Report Generation Works
The system compares the original rate plan costs with the new optimized rate plan costs across all devices, calculating the difference to determine total savings achieved.

### Code Location and Implementation
**Primary Method**: `AltaworxSimCardCostOptimizerCleanup.cs:788-790`

### English Algorithm for Cost Savings Summary Generation

**Step 1: Calculate Statistical Summary**
The system uses the RatePoolStatisticsWriter to generate comprehensive statistics by grouping devices according to their communication plans and analyzing the overall M2M optimization results.

**Step 2: Build Detailed Cost Analysis**
For each rate pool collection in the optimization results:
- The system initializes counters for total original cost and total optimized cost
- It then examines each rate pool within the collection
- For every SIM card in each rate pool, it adds the original cost to the original total and the optimized cost to the optimized total
- After processing all devices, it calculates the total savings by subtracting optimized costs from original costs
- The system also computes the savings percentage by dividing total savings by original costs and multiplying by 100

**Step 3: Generate the Summary Report**
The system creates a comprehensive summary containing:
- The total number of devices processed during optimization
- The total original cost before optimization
- The total optimized cost after optimization
- The absolute dollar amount of savings achieved
- The percentage of cost reduction accomplished
- The billing period information for context

### Cost Calculation Process

**Step 1: Determine Original Costs**
For each device, the system calculates the original cost by adding the base rate amount to any overage charges that would have been incurred under the original rate plan.

**Step 2: Calculate Optimized Costs**
The system finds the rate pool where the device is now assigned based on the optimization results, then calculates what the cost would be in that new rate pool based on the device's actual usage patterns.

**Step 3: Determine Savings**
The savings amount is calculated by subtracting the optimized cost from the original cost. The system also calculates the percentage savings by dividing the savings amount by the original cost and multiplying by 100.

**Step 4: Store All Results**
The system stores the original cost, optimized cost, and savings amount for each device, which allows for detailed reporting and verification of the optimization results.

---

## 3. Rate Plan Utilization Statistics

### What This Report Does
Rate Plan Utilization Statistics analyze how effectively each rate plan is being used after optimization. The report shows usage patterns, capacity utilization percentages, and efficiency metrics for every rate plan in the optimization.

### Why This Report is Important
- **Optimization Validation**: Confirms that rate plans are being used efficiently and that the optimization logic is working correctly
- **Capacity Planning**: Shows which rate plans are over-utilized or under-utilized, helping with future capacity planning decisions
- **Plan Effectiveness**: Identifies which rate plans are performing best and which might need adjustment or replacement
- **Strategic Planning**: Provides data for making informed decisions about rate plan strategies and carrier negotiations

### How the Report Generation Works
The system analyzes device usage patterns against rate plan capacities and costs, calculating utilization percentages and efficiency scores for each rate plan.

### Code Location and Implementation
**Primary Method**: `AltaworxSimCardCostOptimizerCleanup.cs:1376-1390`

### English Algorithm for Rate Plan Utilization Statistics

**Step 1: Initialize Utilization Tracking**
The system creates a tracking dictionary that will store utilization metrics for each rate plan, using the rate plan ID as the key to organize the data.

**Step 2: Process Each Rate Pool**
For every rate pool in the optimization results:
- The system creates a new metrics object to store utilization data
- It counts the total number of devices assigned to this rate pool
- It calculates the total data usage by adding up the usage from all SIM cards in the pool
- It retrieves the plan capacity (data allowance) from the rate plan definition

**Step 3: Calculate Utilization Percentage**
If the rate plan has a defined capacity greater than zero:
- The system calculates utilization percentage by dividing total usage by plan capacity and multiplying by 100
- If the plan has unlimited capacity, the utilization percentage is set to zero

**Step 4: Calculate Efficiency Metrics**
The system computes several efficiency indicators:
- Average usage per device by dividing total usage by the number of devices
- Count of underutilized devices (those using less than 30% of plan capacity)
- Count of overutilized devices (those using more than the plan capacity)

**Step 5: Calculate Cost Efficiency**
The system determines cost efficiency by:
- Calculating cost per megabyte by dividing the monthly rate by the data allowance
- Computing effective cost per megabyte by dividing total charges by actual usage
- This helps identify whether devices are getting good value from their rate plans

**Step 6: Generate Utilization Report**
For each rate plan and its associated metrics:
- The system creates a report entry with the rate plan code
- It includes device count, total usage, utilization percentage, and average usage
- It calculates an efficiency score and generates recommendations for optimization

### Efficiency Calculation Process

**Step 1: Calculate Base Efficiency**
The system determines base efficiency using utilization percentage:
- If utilization is between 70% and 95%, efficiency is rated at 100% (optimal range)
- If utilization is below 70%, efficiency is reduced proportionally
- If utilization exceeds 95%, efficiency is penalized due to potential overage costs

**Step 2: Apply Device Distribution Penalty**
The system calculates usage variance among devices in the rate pool:
- If there's high variance in device usage patterns, it applies a 10% penalty to efficiency
- This identifies rate pools where devices might be better suited to different plans

**Step 3: Apply Cost Efficiency Factor**
The system compares the standard cost per megabyte with the effective cost per megabyte:
- It multiplies the base efficiency by the cost efficiency ratio
- This ensures that cost-effective rate plans receive higher efficiency scores

**Step 4: Final Efficiency Score**
The system ensures the final efficiency score doesn't exceed 100%, providing a standardized metric for comparing rate plan performance.

---

## 4. Optimization Group Details

### What This Report Does
Optimization Group Details provide comprehensive information about how devices are grouped for optimization purposes. The report shows group composition, performance metrics, and specific recommendations for each optimization group.

### Why This Report is Important
- **Group Performance Analysis**: Shows how well each optimization group is performing in terms of cost savings and efficiency
- **Rebalancing Insights**: Identifies optimization groups that might need device reallocation or strategy adjustment
- **Strategy Validation**: Confirms whether the current grouping strategy is effective or needs modification
- **Operational Planning**: Provides data for making decisions about group management and optimization strategies

### How the Report Generation Works
The system analyzes devices grouped by communication plans or customer-defined groups, then calculates group-level performance metrics and generates recommendations.

### Code Location and Implementation
**Primary Method**: `AltaworxSimCardCostOptimizerCleanup.cs:1150-1189`

### English Algorithm for Optimization Group Details

**Step 1: Get Rate Pool Collections**
The system retrieves rate plans specific to the portal type (M2M in this case) and gets mappings between rate plans and rate pools for the optimization queues being processed.

**Step 2: Build Group Structure**
The system generates result rate pools from the available rate plans and their mappings:
- If this is a customer optimization, it also retrieves cross-customer rate plans
- It filters out any duplicate plans to avoid double-counting
- If cross-customer plans exist, it adds them to the rate pools with shared pool designation

**Step 3: Analyze Each Group**
For every rate pool (which represents an optimization group):
- The system creates a new group detail object to store analysis results
- It records basic group information like ID, name, and type (customer-specific or cross-customer)
- It counts the total number of devices in the group

**Step 4: Perform Usage Analysis**
For each group, the system:
- Calculates total data usage by summing usage from all SIM cards in the group
- Determines average usage per device by dividing total usage by device count
- Calculates usage variance to understand how similar device usage patterns are within the group

**Step 5: Conduct Cost Analysis**
The system performs detailed cost analysis:
- Sums up the total original cost for all devices in the group
- Calculates the total optimized cost after optimization
- Determines total savings by subtracting optimized cost from original cost
- Computes savings percentage to show the group's optimization effectiveness

**Step 6: Calculate Performance Metrics**
The system generates advanced performance indicators:
- Optimization effectiveness score based on savings and efficiency
- Group cohesion score showing how well devices fit together
- Recommended actions based on the group's performance characteristics

**Step 7: Generate Group Summary**
After analyzing all groups, the system creates an overall summary:
- Total number of optimization groups processed
- Total devices across all groups
- Identification of the best-performing group based on savings percentage
- Recommendations for rebalancing groups if needed

### Group Performance Calculation Process

**Step 1: Calculate Usage Cohesion**
The system analyzes how similar device usage patterns are within the group:
- It creates a list of usage amounts for all SIM cards in the group
- Calculates the average usage and variance from that average
- Computes the coefficient of variation to measure usage consistency
- Converts this to a cohesion score where higher scores indicate more similar usage patterns

**Step 2: Calculate Cost Effectiveness**
The system determines how cost-effective the group's optimization was:
- It calculates average savings per device by dividing total savings by device count
- Compares this to industry benchmark savings to provide context
- Generates a cost effectiveness percentage showing performance relative to standards

**Step 3: Calculate Optimization Score**
The system creates a composite optimization score:
- Calculates a utilization score based on how well the group uses its rate plan capacity
- Determines a savings score based on the percentage of cost reduction achieved
- Combines these scores with appropriate weighting (40% utilization, 60% savings)

**Step 4: Generate Recommendations**
Based on the calculated metrics, the system generates specific recommendations:
- If usage cohesion is low, it suggests splitting the group due to high usage variance
- If cost effectiveness is below standards, it recommends reviewing rate plan selection
- If utilization scores are low, it suggests the group might benefit from a different rate plan strategy

---

## Complete M2M Report Generation Flow

### Overall Process Description
The complete M2M report generation process integrates all four report types into a single comprehensive Excel file that provides stakeholders with complete visibility into the optimization results.

### English Algorithm for Complete Report Generation

**Step 1: Initialize the Process (Lines 749-754)**
The system sets up two main result containers:
- One for customer-specific optimization results
- Another for cross-customer shared pool results that might involve multiple customers

**Step 2: Prepare Rate Pool Collections (Lines 755-760)**
The system retrieves all necessary rate pool information:
- Gets cross-optimization result rate pools that include shared resources
- Generates customer-specific rate pools by filtering out shared pools
- If this is a customer optimization, adds an unassigned rate pool for devices that couldn't be optimized

**Step 3: Process All Optimization Queues (Lines 761-777)**
The system initializes a flag to track whether cross-pooling results exist, then for each queue:
- Retrieves device assignment results using the GetM2MResults method
- Builds the main optimization result by adding devices to customer-specific rate pools
- Gets shared pool results using GetM2MSharedPoolResults method
- If shared pool results exist, sets the cross-pooling flag and combines all results
- Builds cross-customer optimization results for comprehensive analysis

**Step 4: Generate All Report Components (Lines 779-797)**
The system creates multiple report files:
- Generates cost savings and rate plan utilization statistics using RatePoolStatisticsWriter
- Creates device assignment files using RatePoolAssignmentWriter
- If cross-pooling exists, generates additional statistics and assignment files for shared pools

**Step 5: Create the Final Excel File (Lines 798-800)**
The system combines all generated components:
- Uses GenerateExcelFileFromByteArrays to merge statistics files, assignment files, and shared pool files
- Creates a single comprehensive Excel workbook with multiple tabs for different report types

**Step 6: Save and Return Results (Lines 802-803)**
The system completes the process by:
- Saving the optimization instance result file to the database
- Returning the completed OptimizationInstanceResultFile object for further processing or delivery

This comprehensive process ensures that all stakeholders receive complete, accurate, and actionable information about the M2M optimization results, enabling informed decision-making and successful implementation of optimization recommendations.