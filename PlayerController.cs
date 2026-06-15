using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class PlayerController : NetworkBehaviour {

    public GameObject prefab;

    #region  -------- Inspector --------

    public int GridWidth = 5;        // Number of hexes horizontally
    public int GridHeight = 5;       // Number of hexes vertically
    public float HexRadius = 1f;     // Size of the hexagon 3D model (radius of the "circumcircle")

    [SerializeField] private float height = 0;
    [SerializeField] private float scale = 1;
    #endregion

    //    Rotation 
    //
    //      6 1        
    //     5 0 2       
    //      4 3


    //    Position
    //
    //   0 0 0 0 0      ^
    //    0 0 0 0 0     |
    //   0 0 0 0 0      | Y  Z
    //    0 0 0 0 0     |
    //   0 0 0 0 0      |
    //
    //   --- X ---->


    //     Sequence
    //
    //  0:Idle | 1:Forward1 | 2:Left | 3:Right | 4:Backward | 5:Forward2 | 6:Forward3 | 7:UTurn | 8:Heal | 9:Action
    //
    // 

    //    sequenceSpeed
    //
    // 10 - 255 (x4)   ->  40 - 1020
    //
    // 01 - 99 (x10)   ->  10 - 990 (Visual)  | Unique 

    private static readonly int[] DeckMoveID = { 7, 2, 2, 2, 3, 3, 3, 4, 1, 1, 1, 1, 1, 5, 5, 5, 6, 6 }; // 18
    private static readonly int[] DeckSpeed  = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 }; // 18


    private int rotation;  // [0-6]
    private int xPosition; // [0-255]
    private int yPosition; // [0-255]

    private int sequence;        // 00000000   12345678           <----------------------
    private long sequenceSpeed;  // 00 00 00 00 00 00 00 00   -   11 31 12 01 45 09 42 24

    private List<Vector2Int> movementList = new(); //   (ID,Speed)    (1,11), (2,31), (3,12), ....

    [SerializeField] private int[,] boardCards = new int[8, 2];

    private void Start() {

        movementList = new()
        {
            new(1, 11),
            new(2, 31),
            new(5, 12),
            new(3, 12),
            new(1, 15),
        };
        Vector2Int startingPosition = new Vector2Int(1,0);
        int startingRotation = 2;

        GameObject go = Instantiate(prefab, GridToWorldPosition(startingPosition), Quaternion.Euler(RotationIDToWorldRotation(startingRotation)));
        go.name = "Cube x: " + startingPosition.x + " | y:" + startingPosition.y + " | r:" + startingRotation;

        StartCoroutine(ShowPredictedPath(startingPosition, startingRotation));
    }

    private void SetSequence(List<Vector2Int> moveList) {
        sequence = 0;
        sequenceSpeed = 0;

        for (int i = 0; i < moveList.Count; i++) {

            int moveID = moveList[i].x;
            int speed =  moveList[i].y;

            sequence += moveID * 10 ^ i; // 00000000 -> 00000008 -> 00000078
            sequenceSpeed += speed * 10 ^ (i*2);  // 00 00 00 -> 00 00 11 -> 00 31 11 

        }
    }

    private void AddToMovementList(int moveID , int speed ) {
        Vector2Int move = new Vector2Int( moveID , speed);

        movementList.Add( move );
    }

    private void ClearMovementList() {
        movementList.Clear();   
    }


    private IEnumerator ShowPredictedPath(Vector2Int startingPosition , int startingRotation) {

        Vector2Int newPosition = startingPosition;
        int newRotation = startingRotation;


        for (int i = 0; i < movementList.Count; i++) {

            int moveID = movementList[i].x;

            (newPosition, newRotation) = ExecuteOneMove(moveID, newPosition, newRotation);

            yield return null;

            GameObject go =  Instantiate(prefab, GridToWorldPosition(newPosition), Quaternion.Euler( RotationIDToWorldRotation(newRotation)));
            go.name = "Cube x: " + newPosition.x + " | y:" + newPosition.y + " | r:" + newRotation;
        }       
    }
    public IEnumerator ShowPredictedPath(int[,] boardCards, Vector2Int startingPosition, int startingRotation) {

        Vector2Int newPosition = startingPosition;
        int newRotation = startingRotation;


        for (int i = 0; i < boardCards.GetLength(0); i++) {

            int moveID = boardCards[i,0]; // boardCards[i,1] is the speed 

            (newPosition, newRotation) = ExecuteOneMove(moveID, newPosition, newRotation);

            yield return null;
            // replace by alternateInstanciate
            GameObject go = Instantiate(prefab, GridToWorldPosition(newPosition), Quaternion.Euler(RotationIDToWorldRotation(newRotation)));
            go.name = "Cube x: " + newPosition.x + " | y:" + newPosition.y + " | r:" + newRotation;
        }
    }

    private (Vector2Int Pos, int Rot ) ExecuteOneMove(int moveID, Vector2Int startingPosition, int startingRotation) {
        int tileID = 0; //  which Tile is the next Position / In WorldSpace / ex : 1 Upper Right
        int repeat = 0;

        Vector2Int newPosition = startingPosition;
        int newRotation = startingRotation;
        


        if (moveID == 0) ;
        else if (moveID == 1) tileID = startingRotation;
        else if (moveID == 2) newRotation = TrueModulo(startingRotation - 1, 6); // Rotation
        else if (moveID == 3) newRotation = TrueModulo(startingRotation + 1, 6); // Rotation
        else if (moveID == 4) tileID = TrueModulo(startingRotation + 3, 6);
        else if (moveID == 5) { tileID = startingRotation; repeat = 1; }
        else if (moveID == 6) { tileID = startingRotation; repeat = 2; }
        else if (moveID == 7) newRotation = TrueModulo(startingRotation + 3, 6); // Rotation


        for (int i = 0; i <= repeat; i++) {

            newPosition = MoveOneTile(tileID, newPosition);
        }

        return (newPosition, newRotation);
    }
    private Vector2Int MoveOneTile(int tileID, Vector2Int startingPosition) {
        int x = 0;
        int y = 0;

        if (startingPosition.y % 2 == 1) {

            if (tileID == 1) { x++; y++; }
            if (tileID == 2) { x++; }
            if (tileID == 3) { x++; y--; }

            if (tileID == 4) { y--; }
            if (tileID == 5) { x--; }
            if (tileID == 6) { y++; }
        }
        else {

            if (tileID == 1) { y++; }
            if (tileID == 2) { x++; }
            if (tileID == 3) { y--; }

            if (tileID == 4) { x--; y--; }
            if (tileID == 5) { x--; }
            if (tileID == 6) { x--; y++; }

        }
        return new Vector2Int(x, y) + startingPosition; // if tileID == 0 -> no move
    }

    private int TrueModulo(int a, int b) {
        //Debug.Log("Modulo of : " + a + "% " + b + " is " + (a % b + b) % b);
        return (a % b + b) % b;
    }

    public int[] ExtractDigitsMath(int id) {
        if (id == 0) return new int[] { 0 };

        // Count digits
        int temp = id;
        int digitCount = 0;
        while (temp > 0) {
            digitCount++;
            temp /= 10;
        }

        int[] digits = new int[digitCount];

        // Extract from right to left, then we can reverse if needed
        for (int i = digitCount - 1; i >= 0; i--) {
            digits[i] = id % 10;
            id /= 10;
        }

        return digits; // Returns [5, 4, 1, 7]
    }

    public int DigitsToInt(int[] digits) {
        if (digits == null || digits.Length == 0)
            return 0;

        int result = 0;

        for (int i = 0; i < digits.Length; i++) {
            result = result * 10 + digits[i];
        }

        return result;
    }

    private Vector3 GridToWorldPosition(Vector2Int gridPosition) {

        float offsetX = HexRadius * 2 * scale;
        float offsetY = HexRadius * 1.73205080757f * scale; // root(3) = 1.73...

        float xPos = gridPosition.x * offsetX;
        float yPos = gridPosition.y * offsetY;

        if (gridPosition.y % 2 == 1) {  // Offset every odd lignes
            xPos += HexRadius * scale;
        }

        return  new Vector3(xPos, .8f, yPos);
    }

    private Vector3 RotationIDToWorldRotation(int rotationID) {

        float snappedY = (rotationID - 1) * 60f;

        return new Vector3(0, snappedY, 0);
    }

    //// La "vraie" position est un int, pas un float
    //public NetworkVariable<Vector2Int> GridPosition = new(
    //    writePerm: NetworkVariableWritePermission.Server
    //);

    //private void Awake() {
    //    // Sync visuelle : quand la valeur change, on anime localement
    //    GridPosition.OnValueChanged += OnGridPositionChanged;
    //}

    //private void OnGridPositionChanged(Vector2Int prev, Vector2Int next) {
    //    // Simple lerp visuel côté client — pas de NetworkTransform
    //    StopAllCoroutines();
    //    StartCoroutine(AnimateMove(GridToWorld(prev), GridToWorld(next)));
    //}

    //private IEnumerator AnimateMove(Vector3 from, Vector3 to) {
    //    float t = 0f;
    //    while (t < 1f) {
    //        t += Time.deltaTime / 0.3f; // 0.3s par step
    //        transform.position = Vector3.Lerp(from, to, t);
    //        yield return null;
    //    }
    //    transform.position = to;
    //}

    //// Appelé par le GameManager côté serveur uniquement
    //public void ApplyMove(MoveType move) {
    //    if (!IsServer) return;
    //    Vector2Int next = ComputeNextPosition(GridPosition.Value, move);
    //    GridPosition.Value = next; // ? déclenche OnValueChanged sur tous les clients
    //}

    //private Vector3 GridToWorld(Vector2Int grid) =>
    //    new Vector3(grid.x * cellSize, 0, grid.y * cellSize);





}