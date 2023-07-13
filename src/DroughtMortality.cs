﻿//  Authors:  Samuel W. Flake, Robert M. Scheller

using Landis.Core;
using Landis.SpatialModeling;
using Landis.Utilities;
using Landis.Library.Succession;
using Landis.Library.LeafBiomassCohorts;
using System.Collections.Generic;
using System;
using System.Linq;
using Landis.Library.Climate;

namespace Landis.Extension.Succession.NECN
{
    /// <summary>
    /// Functions (optional) related to drought mortality, using either linear regression or threshold for CWD
    /// </summary>
    public class DroughtMortality
    {
        //Drought species params        
        public static Landis.Library.Parameters.Species.AuxParm<int> CWDThreshold;
        public static Landis.Library.Parameters.Species.AuxParm<double> MortalityAboveThreshold;
        public static Landis.Library.Parameters.Species.AuxParm<int> CWDThreshold2;
        public static Landis.Library.Parameters.Species.AuxParm<double> MortalityAboveThreshold2;
        public static Landis.Library.Parameters.Species.AuxParm<double> Intercept;
        public static Landis.Library.Parameters.Species.AuxParm<double> BetaAge;
        public static Landis.Library.Parameters.Species.AuxParm<double> BetaTemp;
        public static Landis.Library.Parameters.Species.AuxParm<double> BetaSWAAnom;
        public static Landis.Library.Parameters.Species.AuxParm<double> BetaBiomass;
        public static Landis.Library.Parameters.Species.AuxParm<double> BetaCWD;
        public static Landis.Library.Parameters.Species.AuxParm<double> BetaNormCWD;
        public static Landis.Library.Parameters.Species.AuxParm<double> BetaNormTemp;
        public static Landis.Library.Parameters.Species.AuxParm<double> IntxnCWD_Biomass;  // needs better variable name

        public static bool UseDrought = false;
        public static bool OutputSoilWaterAvailable;
        public static bool OutputClimateWaterDeficit;
        public static bool OutputTemperature;
        public static bool WriteSpeciesDroughtMaps;

        public static Landis.Library.Parameters.Ecoregions.AuxParm<AnnualClimate_Monthly> SpinUpWeather;

        //---------------------------------------------------------------------
        // This initialize will be called from PlugIn, dependent on whether drought=true.
        public static void Initialize(IInputParameters parameters)
        {
            CWDThreshold = parameters.CWDThreshold;
            MortalityAboveThreshold = parameters.MortalityAboveThreshold;
            CWDThreshold2 = parameters.CWDThreshold2;
            MortalityAboveThreshold2 = parameters.MortalityAboveThreshold2;
            Intercept = parameters.Intercept;
            BetaAge = parameters.BetaAge;
            BetaTemp = parameters.BetaTemp;
            BetaSWAAnom = parameters.BetaSWAAnom;
            BetaBiomass = parameters.BetaBiomass;
            BetaCWD = parameters.BetaCWD;
            BetaNormCWD = parameters.BetaNormCWD;
            BetaNormTemp = parameters.BetaNormTemp;
            IntxnCWD_Biomass = parameters.IntxnCWD_Biomass;

            OutputSoilWaterAvailable = parameters.OutputSoilWaterAvailable;
            OutputClimateWaterDeficit = parameters.OutputClimateWaterDeficit;
            OutputTemperature = parameters.OutputTemp;
            WriteSpeciesDroughtMaps = parameters.WriteSpeciesDroughtMaps;
            PlugIn.ModelCore.UI.WriteLine("UseDrought on initialization = {0}", UseDrought); //debug

            SpinUpWeather = new Landis.Library.Parameters.Ecoregions.AuxParm<AnnualClimate_Monthly>(PlugIn.ModelCore.Ecoregions);
            int[] spinupYears = new int[10] {0, 1, 2, 3, 4, 5, 6, 7, 8, 9};
            int[] months = new int[12] { 6, 7, 8, 9, 10, 11, 0, 1, 2, 3, 4, 5 };

            //Borrowed from ClimateRegionData.cs
            //Initialize spinup climate data
            foreach (IEcoregion ecoregion in PlugIn.ModelCore.Ecoregions)
            {
                if (ecoregion.Active)
                {
                    //Climate.GenerateEcoregionClimateData(ecoregion, 0, PlugIn.Parameters.Latitude);
                    DroughtMortality.SetSingleAnnualClimate(ecoregion, 0, Climate.Phase.SpinUp_Climate);  // Some placeholder data to get things started.
                }
            }

            foreach (int year in spinupYears)
            {
                SetAllEcoregions_SpinupAnnualClimate(year);
            }

            foreach(int year in spinupYears)
            {
                foreach(int month in months)
                {
                    foreach (ActiveSite site in PlugIn.ModelCore.Landscape.ActiveSites)
                    {
                        SpinUpWater(year, month, site);
                    }
                }

            }

        }

        private static void SetSingleAnnualClimate(IEcoregion ecoregion, int year, Climate.Phase spinupOrfuture)
        {
            int actualYear = Climate.Spinup_MonthlyData.Keys.Min() + year;
            SpinUpWeather[ecoregion] = Climate.Spinup_MonthlyData[actualYear][ecoregion.Index];
        }

        private static void SetAllEcoregions_SpinupAnnualClimate(int year)
        {
            int actualYear = Climate.Future_MonthlyData.Keys.Min() + year - 1;
            foreach (IEcoregion ecoregion in PlugIn.ModelCore.Ecoregions)
            {
                if (ecoregion.Active)
                {
                    //PlugIn.ModelCore.UI.WriteLine("Retrieving {0} for year {1}.", spinupOrfuture.ToString(), actualYear);
                    if (Climate.Spinup_MonthlyData.ContainsKey(actualYear))
                    {
                        SpinUpWeather[ecoregion] = Climate.Spinup_MonthlyData[actualYear][ecoregion.Index];
                    }

                    //PlugIn.ModelCore.UI.WriteLine("Utilizing Climate Data: Simulated Year = {0}, actualClimateYearUsed = {1}.", actualYear, AnnualWeather[ecoregion].Year);
                }

            }
        }

        private static void SpinUpWater(int year, int month, Site site)
        {
            //Stripped down version of the soilwater algorithm to spin up variables for drought mortality

            //...Initialize Local Variables
            double addToSoil = 0.0;
            double bareSoilEvap = 0.0;
            double baseFlow = 0.0;
            double snow = 0.0;
            double stormFlow = 0.0;
            double AET = 0.0; //tracks AET. This variable is no longer subtracted from soilWater, but instead tracks subtractions from soilWater due to other causes.
            double remainingPET = 0.0;
            double availableWaterMax = 0.0;  //amount of water available after precipitation and snowmelt (over-estimate of available water)
            double availableWaterMin = 0.0;   //amount of water available after stormflow (runoff) evaporation and transpiration, but before baseflow/leaching (under-estimate of available water). SF changed to after baseflow
            double plantAvailableWater = 0.0;     //amount of water deemed available to the trees, which will be the average between the max and min
            double priorWaterAvail = SiteVars.PlantAvailableWater[site];
            double waterContentMax = 0.0; //maximum amount of water (before evaporation and ET)

            //...Calculate external inputs
            IEcoregion ecoregion = PlugIn.ModelCore.Ecoregion[site];

            //double litterBiomass = (SiteVars.SurfaceStructural[site].Carbon + SiteVars.SurfaceMetabolic[site].Carbon) * 2.0;
            //double deadBiomass = SiteVars.SurfaceDeadWood[site].Carbon / 0.47;
            
            double soilWaterContent = SiteVars.SoilWaterContent[site];
            if (OtherData.CalibrateMode)
                PlugIn.ModelCore.UI.WriteLine("    SoilWater:  Initial soil water = {0}", soilWaterContent);
            double liquidSnowpack = SiteVars.LiquidSnowPack[site];

            double Precipitation = DroughtMortality.SpinUpWeather[ecoregion].MonthlyPrecip[month];
            double tave = DroughtMortality.SpinUpWeather[ecoregion].MonthlyTemp[month];
            double tmax = DroughtMortality.SpinUpWeather[ecoregion].MonthlyMaxTemp[month];
            double tmin = DroughtMortality.SpinUpWeather[ecoregion].MonthlyMinTemp[month];
            double PET = DroughtMortality.SpinUpWeather[ecoregion].MonthlyPET[month];
            
            if (OtherData.CalibrateMode)
                PlugIn.ModelCore.UI.WriteLine("   SoilWater:  month={0}, tave = {1}, tmax = {2}, tmin = {3}, PET={4}.",
                month, tave, tmax, tmin, PET);
            int daysInMonth = AnnualClimate.DaysInMonth(month, year);
            int beginGrowing = DroughtMortality.SpinUpWeather[ecoregion].BeginGrowing;
            int endGrowing = DroughtMortality.SpinUpWeather[ecoregion].EndGrowing;

            double wiltingPoint = SiteVars.SoilWiltingPoint[site];
            double soilDepth = SiteVars.SoilDepth[site];
            double fieldCapacity = SiteVars.SoilFieldCapacity[site];
            double stormFlowFraction = SiteVars.SoilStormFlowFraction[site];
            double baseFlowFraction = SiteVars.SoilBaseFlowFraction[site];
            double drain = SiteVars.SoilDrain[site];

            double waterFull = soilDepth * fieldCapacity;  //units of cm
            double waterEmpty = wiltingPoint * soilDepth;  // cm

            //PlugIn.ModelCore.UI.WriteLine("waterFull = {0}, waterEmpty = {1}", waterFull, waterEmpty); //debug

            if (waterFull == waterEmpty || wiltingPoint > fieldCapacity)
            {
                throw new ApplicationException(string.Format("Field Capacity and Wilting Point EQUAL or wilting point {0} > field capacity {1}.  Row={2}, Column={3}", wiltingPoint, fieldCapacity, site.Location.Row, site.Location.Column));
            }

            //Adjust PET at the pixel scale for slope and aspect following Bugmann 1994, p 82 and Schumacher 2004, p. 114.
            //If slope and aspect are not provided, then SlAsp is zero and has no effect on PET
            //First calculate slope and aspect modifier (SlAsp)

            double slope = SiteVars.Slope[site];
            double aspect = SiteVars.Aspect[site];
            double SlAsp = SoilWater.CalculateSlopeAspectEffect(slope, aspect);

            if (SlAsp > 0)
            {
                PET = PET * (1 + SlAsp * 0.125);
            }
            else
            {
                PET = PET * (1 + SlAsp * 0.063);
            }

            if (OtherData.CalibrateMode) PlugIn.ModelCore.UI.WriteLine("   Slope = {0}, Aspect = {1}, SlAsp = {2}, Adjusted PET = {3}",
                    slope, aspect, SlAsp, PET);

            //...Calculating snow pack first. Occurs when mean monthly air temperature is equal to or below freezing,
            //     precipitation is in the form of snow.

            if (tmin <= 0.0) // Use tmin to dictate whether it snows or rains. 
            {
                snow = Precipitation;
                Precipitation = 0.0;
                liquidSnowpack += snow;  //only tracking liquidsnowpack (water equivalent) and not the actual amount of snow on the ground (i.e. not snowpack).
                                            //PlugIn.ModelCore.UI.WriteLine("Let it snow!! snow={0}, liquidsnowpack={1}.", snow, liquidSnowpack);
            }
            else
            {
                soilWaterContent += Precipitation;
                //PlugIn.ModelCore.UI.WriteLine("Let it rain and add it to soil! rain={0}, soilWaterContent={1}.", Precipitation, soilWaterContent);
            }


            //Set original remainingPET outside of the if(snow) sections, in case all the snow is melted
            remainingPET = PET;
            if (OtherData.CalibrateMode)
                PlugIn.ModelCore.UI.WriteLine("   SoilWater: snowpack before melting = {0}", liquidSnowpack);
            //...Then melt snow if there is snow on the ground and air temperature (tmax) is above minimum.            
            if (liquidSnowpack > 0.0 && tmax > 0.0)
            {
                //...Calculate the amount of snow to melt:
                //This relationship ultimately derives from http://www.nps.gov/yose/planyourvisit/climate.htm which described the relationship between snow melting and air temp.
                //Documentation for the regression equation is in spreadsheet called WaterCalcs.xls by M. Lucash
                double snowMeltFraction = Math.Max((tmax * 0.05) + 0.024, 0.0);//This equation assumes a linear increase in the fraction of snow that melts as a function of air temp.  

                if (snowMeltFraction > 1.0)
                    snowMeltFraction = 1.0;

                addToSoil = liquidSnowpack * snowMeltFraction;  //Amount of liquidsnowpack that melts = liquidsnowpack multiplied by the fraction that melts.
                if (addToSoil > liquidSnowpack) addToSoil = liquidSnowpack;

                PlugIn.ModelCore.UI.WriteLine("   Snow melts, addToSoil = {0}", addToSoil);

                // Subtracted melted snow from snowpack and add it to the soil
                // We are not accounting for evaporation from snow ablation
                liquidSnowpack = Math.Max(liquidSnowpack - addToSoil, 0.0);
                soilWaterContent += addToSoil;
            }
            if (OtherData.CalibrateMode)
                PlugIn.ModelCore.UI.WriteLine("   Snow before evaporation = {0}", liquidSnowpack);
            //...Evaporate water from the snow pack (rewritten by Pulliam 9/94)
            //...Coefficient 0.87 relates to heat of fusion for ice vs. liquid water
            if (liquidSnowpack > 0.0)
            {
                //...Calculate cm of snow that remaining pet energy can evaporate:
                double evaporatedSnow = PET * 0.87;

                //...Don't evaporate more snow than actually exists:
                if (evaporatedSnow > liquidSnowpack)
                    evaporatedSnow = liquidSnowpack;

                liquidSnowpack = liquidSnowpack - evaporatedSnow;

                //...Decrement remaining pet by energy used to evaporate snow:
                remainingPET = Math.Max(remainingPET - evaporatedSnow / 0.87, 0.0);
                AET += evaporatedSnow / 0.87;

                if (OtherData.CalibrateMode)
                    PlugIn.ModelCore.UI.WriteLine("   SoilWater:  month={0}, Remaining PET after evaporation ={1}.", month, remainingPET);

            }

            if (OtherData.CalibrateMode)
                PlugIn.ModelCore.UI.WriteLine("   snow after evaporation = {0}", liquidSnowpack);
            //...Calculate bare soil water loss and interception  when air temperature is above freezing and no snow cover.
            //...Mofified 9/94 to allow interception when t < 0 but no snow cover, Pulliam
            if (liquidSnowpack <= 0.0)
            {
                //...Calculate total canopy cover and litter, put cap on effects:
                double standingBiomass = 800;
                double litterBiomass = 400.0;

                //if (standingBiomass > 800.0) standingBiomass = 800.0;
                //if (litterBiomass > 400.0) litterBiomass = 400.0;

                //...canopy interception, fraction of  precip (canopyIntercept):
                double canopyIntercept = ((0.0003 * litterBiomass) + (0.0006 * standingBiomass)) * OtherData.WaterLossFactor1;

                //...Bare soil evaporation, fraction of precip (bareSoilEvap):
                bareSoilEvap = 0.5 * System.Math.Exp((-0.002 * litterBiomass) - (0.004 * standingBiomass)) * OtherData.WaterLossFactor2;

                //...Calculate total surface evaporation losses, maximum allowable is 0.4 * pet. -rm 6/94
                double soilEvaporation = System.Math.Min(((bareSoilEvap + canopyIntercept) * Precipitation), (0.4 * remainingPET));

                soilEvaporation = System.Math.Min(soilEvaporation, soilWaterContent);
                if (OtherData.CalibrateMode)
                    PlugIn.ModelCore.UI.WriteLine("   soilEvaporation = {0}, bareSoilEvap = {1}, canopyIntercept = {2}",
                        soilEvaporation, bareSoilEvap, canopyIntercept);

                //Subtract soil evaporation from soil water content
                soilWaterContent = Math.Max(soilWaterContent - soilEvaporation, 0.0); // Do not allow to go negative
                AET += soilEvaporation;
                remainingPET = Math.Max(remainingPET - soilEvaporation, 0.0);

            }


            //Calculate the max amout of water available to trees, an over-estimate of the water available to trees.  It only reflects precip and melting of precip.
            availableWaterMax = Math.Max(soilWaterContent - waterEmpty, 0.0);
            waterContentMax = soilWaterContent;
            if (OtherData.CalibrateMode)
                PlugIn.ModelCore.UI.WriteLine("   Max soil water (after adding ppt and snowmelt, " +
                "and substracting soil evaporation) = {0}", waterContentMax); //debug

            //Allow excess water to run off when soil is greater than field capacity
            double waterMovement = 0.0;

            if (soilWaterContent > waterFull)
            {
                // How much water should move during a storm event, which is based on how much water the soil can hold.
                waterMovement = Math.Max((soilWaterContent - waterFull), 0.0);

                //...Compute storm flow.
                stormFlow = waterMovement * stormFlowFraction;

                //Subtract stormflow from soil water
                // Remove excess water (above field capacity) as stormflow; the rest remains available until end of month. SWC can still be greater than FC at this point
                soilWaterContent = Math.Max(soilWaterContent - stormFlow, 0.0);

                //PlugIn.ModelCore.UI.WriteLine("Water Runs Off. stormflow={0}. soilWaterContent = {1}", stormFlow, soilWaterContent); //debug
            }

            // Calculate actual evapotranspiration.  This equation is derived from the stand equation for calculating AET from PET
            //  Bergström, 1992
            double tempAET = 0.0;

            if (OtherData.CalibrateMode) PlugIn.ModelCore.UI.WriteLine("   Before calculating AET: Month={0}, soilWaterContent = {1}, remainingPET = {2}, waterFull = {3}, waterEmpty = {4}.",
                month, soilWaterContent, remainingPET, waterFull, waterEmpty);

            if (soilWaterContent - waterEmpty >= remainingPET)

            {
                tempAET = remainingPET;
            }
            else
            {
                //otherwise AET is limited by soil water
                tempAET = Math.Min(remainingPET * ((soilWaterContent - waterEmpty) / (waterFull - waterEmpty)), soilWaterContent - waterEmpty);
            }

            /*Here's the original equation from Bergstom (1992) HBV model: https://github.com/mxgiuliani00/hbv/blob/master/hbv_model.cpp
            It allows soil moisture to be drawn down to zero, with it becoming more difficult when soil moisture is below waterEmpty
                {
                    evap = remainingPET * Math.Min((soilWaterContent / waterEmpty), 1.0);
                    evap = Math.Min(evap, 0.0);
                    if (soilWaterContent < evap) {
                        evap = soilWaterContent;
                    }
                }
            */


            //PlugIn.ModelCore.UI.WriteLine("Month={0}, soilWaterContent = {1}, waterEmpty = {2}, waterFull = {3}.", month, soilWaterContent, waterFull, waterEmpty);
            //Subtract ET from soil water content
            soilWaterContent = Math.Max(soilWaterContent - tempAET, 0.0);
            AET = Math.Max(AET + tempAET, 0.0);
            remainingPET = Math.Max(remainingPET - tempAET, 0.0);
            //PlugIn.ModelCore.UI.WriteLine("tempAET = {0}, AET = {1}, remainingPET = {2}, soilWaterContent = {3}", 
            //    tempAET, AET, remainingPET, soilWaterContent); //debug

            //PlugIn.ModelCore.UI.WriteLine("AET = {0}. soilWaterContent = {1}", AET, soilWaterContent); //debug

            //Water above permanent wilting point can be drained by baseflow; otherwise, baseflow does not occur
            double remainingWater = Math.Max(soilWaterContent - waterEmpty, 0.0);

            //Leaching occurs. Drain baseflow fraction from soil water
            baseFlow = remainingWater * baseFlowFraction; //Calculate baseflow as proportion of water above permanent wilt point
            baseFlow = Math.Max(baseFlow, 0.0); // make sure baseflow > 0 
            soilWaterContent = Math.Max(soilWaterContent - baseFlow, 0.0);  //remove baseFlow from soil water

            if (OtherData.CalibrateMode)
                PlugIn.ModelCore.UI.WriteLine("   Baseflow = {0}. soilWaterContent = {1}", baseFlow, soilWaterContent);

            // Limit soil moisture that carries over to the next month.
            // Soil moisture carried forward cannot exceed field capacity; everything else runs off and is added to baseflow
            double surplus = Math.Max(soilWaterContent - waterFull, 0.0); //calculate water in excess of field capacity
            baseFlow += surplus; //add runoff to baseFlow to calculate leaching
            soilWaterContent = Math.Max(soilWaterContent - surplus, 0.0);
            availableWaterMin = Math.Max(soilWaterContent - waterEmpty, 0.0); //minimum amount of water for the month

            // Calculate the final amount of available water to the trees, which is the average of the max and min 
            plantAvailableWater = (availableWaterMax + availableWaterMin) / 2.0;//availableWaterMax is the initial soilWaterContent after precip, interception, and bare-soil evaporation  

            // SF added meanSoilWater as variable to calculate volumetric water to compare to empirical sources
            // such as FluxNet or Climate Reference Network data. Actual end-of-month soil moisture is tracked in SoilWaterContent.
            double meanSoilWater = (waterContentMax + soilWaterContent) / 2.0;
            //PlugIn.ModelCore.UI.WriteLine("   availableWaterMax = {0}, soilWaterContent = {1}", availableWaterMax, soilWaterContent);

            // Compute the ratio of precipitation to PET
            double ratioPlantAvailableWaterPET = 0.0;
            if (PET > 0.0) ratioPlantAvailableWaterPET = plantAvailableWater / PET;
            //PlugIn.ModelCore.UI.WriteLine("Precip={0}, PET={1}, Ratio={2}.", Precipitation, PET, ratioPlantAvailableWaterPET); //debug

            SiteVars.AnnualWaterBalance[site] += Precipitation - AET;
            SiteVars.MonthlyClimaticWaterDeficit[site][Main.Month] = (PET - AET) * 10.0;
            SiteVars.MonthlyActualEvapotranspiration[site][Main.Month] = AET;
            SiteVars.AnnualClimaticWaterDeficit[site] += (PET - AET) * 10.0;  // Convert to mm, the standard definition
            SiteVars.AnnualPotentialEvapotranspiration[site] += PET * 10.0;  // Convert to mm, the standard definition
                                                                                //PlugIn.ModelCore.UI.WriteLine("Month={0}, PET={1}, AET={2}.", month, PET, AET); //debug
            
            SiteVars.LiquidSnowPack[site] = liquidSnowpack;
            SiteVars.WaterMovement[site] = waterMovement;
            SiteVars.PlantAvailableWater[site] = plantAvailableWater;  //available to plants for growth     
            SiteVars.SoilWaterContent[site] = soilWaterContent; //lowest (end-of-month) soil water, after subtracting everything
            SiteVars.MeanSoilWaterContent[site] = meanSoilWater; //mean of lowest soil water and highest soil water
            SiteVars.MonthlySoilWaterContent[site][Main.Month] = soilWaterContent;
            SiteVars.MonthlyMeanSoilWaterContent[site][Main.Month] = meanSoilWater / soilDepth; //Convert to volumetric water content

            return;
        
        }

        public static class SpeciesMapNames
        {
            public const string SpeciesVar = "species";
            public const string TimestepVar = "timestep";

            private static IDictionary<string, bool> knownVars;
            private static IDictionary<string, string> varValues;

            //---------------------------------------------------------------------

            static SpeciesMapNames()
            {
                knownVars = new Dictionary<string, bool>();
                knownVars[SpeciesVar] = true;
                knownVars[TimestepVar] = true;

                varValues = new Dictionary<string, string>();
            }

            //---------------------------------------------------------------------

            public static void CheckTemplateVars(string template)
            {
                OutputPath.CheckTemplateVars(template, knownVars);
            }

            //---------------------------------------------------------------------

            public static string ReplaceTemplateVars(string template,
                                                     string species,
                                                     int timestep)
            {
                varValues[SpeciesVar] = species;
                varValues[TimestepVar] = timestep.ToString();
                return OutputPath.ReplaceTemplateVars(template, varValues);
            }
        }

        public static double[] ComputeDroughtMortality(ICohort cohort, ActiveSite site)
        {

            //Predictor variables
            double normalSWA = SiteVars.NormalSWA[site];
            //PlugIn.ModelCore.UI.WriteLine("normalSWA is {0}", normalSWA);

            double normalCWD = SiteVars.NormalCWD[site];
            //PlugIn.ModelCore.UI.WriteLine("normalCWD is {0}", normalCWD);

            double normalTemp= SiteVars.NormalTemp[site];
            //PlugIn.ModelCore.UI.WriteLine("normalTemp is {0}", normalTemp);

            //TODO ned to divide SoilWater10 by swayear
            double swaAnom = SiteVars.SWALagged[site] - normalSWA;
            //PlugIn.ModelCore.UI.WriteLine("swaAnom is {0}", swaAnom);

            double tempLagged = SiteVars.TempLagged[site];

            double cwdLagged = SiteVars.CWDLagged[site];

            double cohortAge = cohort.Age;
            double siteBiomass = SiteVars.ActualSiteBiomass(site);


            //Equation parameters
            int cwdThreshold = CWDThreshold[cohort.Species];
            //PlugIn.ModelCore.UI.WriteLine("cwdThreshold is {0}", cwdThreshold);

            double mortalityAboveThreshold = MortalityAboveThreshold[cohort.Species];
            int cwdThreshold2 = CWDThreshold2[cohort.Species];
            //PlugIn.ModelCore.UI.WriteLine("cwdThreshold2 is {0}", cwdThreshold2);

            double mortalityAboveThreshold2 = MortalityAboveThreshold2[cohort.Species];

            double intercept = Intercept[cohort.Species];
            double betaAge = BetaAge[cohort.Species];
            double betaTemp = BetaTemp[cohort.Species];
            double betaSWAAnom = BetaSWAAnom[cohort.Species];
            double betaBiomass = BetaBiomass[cohort.Species];
            double betaCWD = BetaCWD[cohort.Species];
            double betaNormCWD = BetaNormCWD[cohort.Species];
            double betaNormTemp = BetaNormTemp[cohort.Species];
            double intxnCWD_Biomass = IntxnCWD_Biomass[cohort.Species];
            //PlugIn.ModelCore.UI.WriteLine("Regression parameters are: intercept {0}, age {1}, temp {2}, SWAAnom {3}, biomass {4}", 
            //    intercept, betaAge, betaTemp, betaSWAAnom, betaBiomass);

            // double mortalitySlope = 0.005; //TODO make this a species-level param

            double p_mort = 0;
            double M_leaf = 0;
            double M_wood = 0;

            if (OtherData.CalibrateMode) PlugIn.ModelCore.UI.WriteLine("lagged CWD is {0}", cwdLagged);

            if (cwdLagged > cwdThreshold & cwdThreshold != 0)
            {
                //p_mort = mortalityAboveThreshold + mortalitySlope * (waterDeficit - cwdThreshold); TODO implement
                p_mort = mortalityAboveThreshold;
                if (OtherData.CalibrateMode) PlugIn.ModelCore.UI.WriteLine("p_mort from CWD is {0}", p_mort);
            }

            if (cwdLagged > cwdThreshold2 & cwdThreshold2 != 0)
            {
                //p_mort = mortalityAboveThreshold + mortalitySlope * (waterDeficit - cwdThreshold); TODO implement
                p_mort = mortalityAboveThreshold2;
                if(OtherData.CalibrateMode) PlugIn.ModelCore.UI.WriteLine("p_mort from CWD2 is {0}", p_mort);
            }


            if (cwdThreshold == 0 & cwdThreshold2 == 0)
            {
                //calculate decadal log odds of survival
                //TODO we need to get the climate vars from a SiteVar, calculated in ComputeDroughtSiteVars
                double logOdds = intercept + betaAge * cohortAge + betaTemp * tempLagged + betaSWAAnom * swaAnom + betaBiomass * siteBiomass +
                    betaCWD * cwdLagged + betaNormCWD * normalCWD + betaNormTemp * normalTemp + intxnCWD_Biomass * cwdLagged * siteBiomass;
                double p_surv = Math.Exp(logOdds) / (Math.Exp(logOdds) + 1);
                p_mort = (1 - Math.Pow(p_surv, 0.1));
                if (OtherData.CalibrateMode) PlugIn.ModelCore.UI.WriteLine("p_mort from regression is {0}", p_mort);

            }

            double random = PlugIn.ModelCore.GenerateUniform();

            if (p_mort > random)
            {

                M_leaf = cohort.LeafBiomass;
                M_wood = cohort.WoodBiomass;
                double aboveground_Biomass_Died = M_leaf + M_wood;

                SiteVars.DroughtMort[site] += aboveground_Biomass_Died;
                SiteVars.SpeciesDroughtMortality[site][cohort.Species.Index] += aboveground_Biomass_Died;

            }

            //SiteVars.Cohorts[site].ReduceOrKillBiomassCohorts(this);
            //PlugIn.ModelCore.UI.WriteLine("CWD is {0}", waterDeficit);
            //PlugIn.ModelCore.UI.WriteLine("Drought wood mortality = {0}. Leaf mortality = {0}.", M_leaf, M_wood);

            double[] M_DROUGHT = new double[2] { M_wood, M_leaf };

            return M_DROUGHT;

        }

        public static void ComputeDroughtLaggedVars(ActiveSite site)
        {
            //TODO this only needs to be computed once per site per year (in month 5)

            int timestep = PlugIn.ModelCore.CurrentTime;

            double waterDeficit = SiteVars.AnnualClimaticWaterDeficit[site];
            //PlugIn.ModelCore.UI.WriteLine("curernt year CWD is {0}", waterDeficit);


            double[] soilWater = new double[0];
            double[] tempValue = new double[0];
            double[] cwdValue = new double[0];

            //For the first few years, use all of the temp and soilwater data, until lags can start to be used
            //TODO use spinup climate?

            //TODO have adjustable timelags as input variable?

            int currentyear = timestep + 1;
            //PlugIn.ModelCore.UI.WriteLine("current year is ", currentyear);
            int swayear = currentyear;
            int tempyear = currentyear;
            int cwdyear = currentyear;
            if (currentyear >= 10)
            {
                currentyear = 10;
                swayear = 8;
                tempyear = 7;
                cwdyear = 10;
            }
            else if (currentyear == 9 | currentyear == 8)
            {
                swayear = 8;
                tempyear = 7;
                cwdyear = currentyear;
            }

            //PlugIn.ModelCore.UI.WriteLine("SoilWater10 is {0}", SiteVars.SoilWater10[site]);

            //soilWater should already have just 10 elements
            soilWater = SiteVars.SoilWater10[site].OrderByDescending(s => s).Reverse().Take(swayear).ToArray();
            tempValue = SiteVars.Temp10[site].OrderByDescending(s => s).Take(tempyear).ToArray();
            cwdValue = SiteVars.cwd10[site].OrderByDescending(s => s).Take(cwdyear).ToArray();

            //PlugIn.ModelCore.UI.WriteLine("Time-lagged CWD is {0}", cwdValue);

            //get SWA8yrs and Temp7yrs           

            //initialize variables for lowest SWA of highest 8 years of 10 and highest temperature for 7 years out of 10
            double SWA8years = 0;
            double Temp7years = 0;
            double CWD10years = 0;

            //Sum values selected above 
            Array.ForEach(soilWater, i => SWA8years += i);
            Array.ForEach(tempValue, i => Temp7years += i);
            Array.ForEach(cwdValue, i => CWD10years += i);


            SWA8years /= swayear;
            Temp7years /= tempyear;
            CWD10years /= cwdyear;

            SiteVars.SWALagged[site] = SWA8years; //TODO can we store these in DroughtMortality instead of SiteVars?
            SiteVars.TempLagged[site] = Temp7years;
            SiteVars.CWDLagged[site] = CWD10years;
            //PlugIn.ModelCore.UI.WriteLine("temp is {0}", Temp7years);
        }

    }
}
