/// <summary>
/// Maps model class ids to gameplay concepts. COCO ids come from YOLOX;
/// Cityscapes train ids come from Fast-SCNN.
/// </summary>
public static class CvClassCatalog
{
    // COCO ids (YOLOX)
    public const int CocoPerson = 0;
    public const int CocoBicycle = 1;
    public const int CocoCar = 2;
    public const int CocoMotorcycle = 3;
    public const int CocoBus = 5;
    public const int CocoTrain = 6;
    public const int CocoTruck = 7;
    public const int CocoTrafficLight = 9;
    public const int CocoFireHydrant = 10;
    public const int CocoStopSign = 11;
    public const int CocoBench = 13;
    public const int CocoPottedPlant = 58;

    // Cityscapes train ids (Fast-SCNN)
    public const int CityRoad = 0;
    public const int CitySidewalk = 1;
    public const int CityVegetation = 8;
    public const int CityTerrain = 9;
    public const int CitySky = 10;
    public const int CityTrain = 16;

    public static bool TryMapCocoToObstacle(int cocoClassId, out ObstacleKind kind)
    {
        switch (cocoClassId)
        {
            case CocoCar:
            case CocoBus:
            case CocoTruck:
            case CocoTrain:
            case CocoMotorcycle:
            case CocoBicycle:
                kind = ObstacleKind.Vehicle;
                return true;
            case CocoPerson:
                kind = ObstacleKind.Person;
                return true;
            case CocoStopSign:
                kind = ObstacleKind.Sign;
                return true;
            case CocoTrafficLight:
                kind = ObstacleKind.TrafficLight;
                return true;
            case CocoFireHydrant:
                kind = ObstacleKind.Hydrant;
                return true;
            case CocoBench:
                kind = ObstacleKind.Bench;
                return true;
            case CocoPottedPlant:
                kind = ObstacleKind.Bush;
                return true;
            default:
                kind = ObstacleKind.Generic;
                return false;
        }
    }

    /// <summary>
    /// Jumpable obstacles are low: the sphere clears them mid-jump, so they
    /// never make a lane impossible, only inconvenient.
    /// </summary>
    public static bool IsJumpable(ObstacleKind kind)
    {
        return kind == ObstacleKind.Hydrant
            || kind == ObstacleKind.Bench
            || kind == ObstacleKind.Bush
            || kind == ObstacleKind.Sign;
    }
}

public enum ObstacleKind
{
    Generic,
    Vehicle,
    Person,
    Sign,
    TrafficLight,
    Hydrant,
    Bench,
    Bush
}
