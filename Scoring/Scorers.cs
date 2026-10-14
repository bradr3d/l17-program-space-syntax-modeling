using System;
using System.IO;

//interface for scoring (each extending class will score one metric)
interface ScoringInterface
{
    //one abstract method for outputting the computed score (some numeric value)
}

//net-to-gross scorer
class NetToGrossScorer : ScoringInterface
{
    
}

//weighted travel distance scorer
class WeightedTravelDistanceScorer : ScoringInterface
{
    
}

//adjacency satisfaction scorer
class AdjacencySatisfactionScorer : ScoringInterface
{
    
}

//area error scorer
class AreaErrorScorer : ScoringInterface
{
    
}

//daylight eligible percentage scorer
class DaylightEligiblePercentScorer : ScoringInterface
{
    
}

//egress travel distance scorer
class EgressTravelDistanceScorer : ScoringInterface
{
    
}

//space syntax integration scorer
class SpaceSyntaxIntegrationScorer : ScoringInterface
{
    
}

//grid alignment scorer
class GridAlignmentScorer : ScoringInterface
{
    
}
