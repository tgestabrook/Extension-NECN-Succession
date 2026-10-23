//  Author: Robert Scheller, Melissa Lucash

using Landis.Core;
using Landis.SpatialModeling;
using Landis.Library.Climate;
using System.Linq;


namespace Landis.Extension.Succession.NECN
{
    public class ClimateRegionData
    {

        public static Landis.Library.Parameters.Ecoregions.AuxParm<int> ActiveSiteCount;
        public static Landis.Library.Parameters.Ecoregions.AuxParm<double> AnnualNDeposition;
        public static Landis.Library.Parameters.Ecoregions.AuxParm<double[]> MonthlyNDeposition;
        public static Landis.Library.Parameters.Ecoregions.AuxParm<AnnualClimate> AnnualClimate;
        public static Landis.Library.Parameters.Ecoregions.AuxParm<AnnualClimate> AnnualClimateSpinup;

        public class ClimateRegionAccumulator
        {
            private readonly double[] annualNDeposition;
            private readonly double[][] monthlyNDeposition;

            public ClimateRegionAccumulator(int ecoregionCount)
            {
                annualNDeposition = new double[ecoregionCount];
                monthlyNDeposition = new double[ecoregionCount][];

                for (int i = 0; i < ecoregionCount; i++)
                {
                    monthlyNDeposition[i] = new double[12];
                }
            }

            public void Accumulate(IEcoregion ecoregion, int month, double deposition)
            {
                int index = ecoregion.Index;
                annualNDeposition[index] += deposition;
                monthlyNDeposition[index][month] += deposition;
            }

            public void Reduce()
            {
                foreach (var ecoregion in PlugIn.ModelCore.Ecoregions.Where(x => x.Active))
                {
                    ClimateRegionData.AnnualNDeposition[ecoregion] += annualNDeposition[ecoregion.Index];
                    double[] localMonthlyNDeposition = monthlyNDeposition[ecoregion.Index];
                    double[] sharedMonthlyNDeposition = ClimateRegionData.MonthlyNDeposition[ecoregion];

                    for (int month = 0; month < 12; month++)
                    {
                        sharedMonthlyNDeposition[month] += localMonthlyNDeposition[month];
                    }
                }
            }
        }

        //---------------------------------------------------------------------
        // NOTE:  Climate data year index is 1-based
        public static void Initialize(IInputParameters parameters)
        {
            ActiveSiteCount = new Landis.Library.Parameters.Ecoregions.AuxParm<int>(PlugIn.ModelCore.Ecoregions);
            AnnualClimate = new Landis.Library.Parameters.Ecoregions.AuxParm<AnnualClimate>(PlugIn.ModelCore.Ecoregions);
            AnnualClimateSpinup = new Landis.Library.Parameters.Ecoregions.AuxParm<AnnualClimate>(PlugIn.ModelCore.Ecoregions);
            MonthlyNDeposition = new Landis.Library.Parameters.Ecoregions.AuxParm<double[]>(PlugIn.ModelCore.Ecoregions);
            AnnualNDeposition = new Landis.Library.Parameters.Ecoregions.AuxParm<double>(PlugIn.ModelCore.Ecoregions);

            foreach (ActiveSite site in PlugIn.ModelCore.Landscape)
            {
                IEcoregion ecoregion = PlugIn.ModelCore.Ecoregion[site];
                ActiveSiteCount[ecoregion]++;
            }

            foreach (IEcoregion ecoregion in PlugIn.ModelCore.Ecoregions)
            {
                MonthlyNDeposition[ecoregion] = new double[12];
            }

            // generate all climate data for all ecoregions 
            Climate.GenerateEcoregionClimateData(PlugIn.Parameters.Latitude);

            // grab the first year's spinup climate
            foreach (var ecoregion in PlugIn.ModelCore.Ecoregions.Where(x => x.Active))
            {
                AnnualClimateSpinup[ecoregion] = Climate.SpinupEcoregionYearClimate[ecoregion.Index][1];      // Climate data year index is 1-based
            }
        }

        //---------------------------------------------------------------------
        // Generates new climate parameters for all ecoregions at an annual time step.

        public static void SetAllEcoregionsFutureAnnualClimate(int year)
        {
            // grab the year's future climate
            foreach (var ecoregion in PlugIn.ModelCore.Ecoregions.Where(x => x.Active))
            {
                AnnualClimate[ecoregion] = Climate.FutureEcoregionYearClimate[ecoregion.Index][year];      // Climate data year index is 1-based
            }

        }
    }
}
