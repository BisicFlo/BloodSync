using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;


public struct ResolvedMove {
    public int ClientId;
    public int Move; 
    public int Speed;
}


public class GameManager : NetworkBehaviour {
    public static GameManager Instance { get; private set; }

    public UnityEvent OnTurnEnd = new UnityEvent();

    [SerializeField] private List<RobotData> robotDataList;  // -> Should merge with RobotSelection

    [SerializeField] private List<PlayerData> PlayerDataList; // [Hide] All Players Infos stored on Host/Server


    public NetworkVariable<byte> ReadyMask = new(
    0,
    NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Server
    ); //    bitmask : bit 0:P1 , bit 1:P2  /!\ byte: 8 players max

    public NetworkVariable<byte> CurrentPhase = new(
    1,
    NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Server
    ); //     0:MainMenu | 1:RobotSelection | 2:Preparation | 3:Play  | 4:Finished


    private Dictionary<ulong, int> _playerIndexMap = new();

    private void Awake() {
        Instance = this;
        //PlayerStates = new NetworkList<ulong>();
        //PlayerSpeeds = new NetworkList<ulong>();
        //CurrentPhase = 0;
    }
    private void Start() {
        // LobbyManager.Instance.OnGameStarted += LobbyManager_OnGameStarted;
    }
  

    #region -------------- Player Index Mapping --------------
    public override void OnNetworkSpawn() {
        // Tous les clients peuvent réagir au changement de ReadyMask 
        ReadyMask.OnValueChanged += OnReadyMaskChanged; //
        CurrentPhase.OnValueChanged += OnCurrentPhaseChanged;

        LobbyManager.Instance.OnGameStarted += LobbyManager_OnGameStarted; // NEW

        if (IsServer) {
            NetworkManager.Singleton.OnClientConnectedCallback += NetworkManager_OnClientConnectedCallback;
            NetworkManager.Singleton.OnClientDisconnectCallback += NetworkManager_OnClientDisconnectedCallback;

            // CurrentPhase.Value = 1; // New 
        }

        PlayerUI.Instance.SetPlayerArrowVisible((int)NetworkManager.Singleton.LocalClientId); //temp // NullRefe
    }
    public override void OnNetworkDespawn() {
        ReadyMask.OnValueChanged -= OnReadyMaskChanged;
        CurrentPhase.OnValueChanged -= OnCurrentPhaseChanged;

        LobbyManager.Instance.OnGameStarted -= LobbyManager_OnGameStarted; // NEW


        if (IsServer) {
            NetworkManager.Singleton.OnClientConnectedCallback -= NetworkManager_OnClientConnectedCallback;
            NetworkManager.Singleton.OnClientDisconnectCallback -= NetworkManager_OnClientDisconnectedCallback;
        }
    }
    private void NetworkManager_OnClientConnectedCallback(ulong clientId) {
        Debug.Log("NetworkManager_OnClientConnectedCallback : " + clientId);
        if (_playerIndexMap.ContainsKey(clientId)) return;
        _playerIndexMap[clientId] = _playerIndexMap.Count;

        // Creating a new RobotData
        PlayerData pd = ScriptableObject.CreateInstance<PlayerData>();
        PlayerDataList.Add(pd);

        // Initialiser RobotData 
        pd.Init((int)clientId, (int)clientId); // Health and Respawn


    }
    private void NetworkManager_OnClientDisconnectedCallback(ulong clientId) {
        Debug.Log("NetworkManager_OnClientDisconnectedCallback : " + clientId);
        if (!_playerIndexMap.TryGetValue(clientId, out int removedIndex)) return;

        _playerIndexMap.Remove(clientId);

        // Réindexer les joueurs au-dessus de l'index supprimé
        foreach (ulong id in _playerIndexMap.Keys.ToList())
            if (_playerIndexMap[id] > removedIndex)
                _playerIndexMap[id]--;
    }
    private int GetPlayerIndex(ulong clientId) {
        return _playerIndexMap.TryGetValue(clientId, out int index) ? index : -1;
    }
    private ulong GetClientIdByPlayerIndex(int playerIndex) { // NEW
        // Reverse lookup in Dictionary 
        foreach (var pair in _playerIndexMap) {
            if (pair.Value == playerIndex)
                return pair.Key;
        }

        return ulong.MaxValue; // Not found (common convention in NGO)
    }
    #endregion

    #region -------------- Event Handlers --------------
    private void OnReadyMaskChanged(byte previous, byte current) {
        int playerCount = NetworkManager.ConnectedClientsIds.Count;
        int allReady = (1 << playerCount) - 1;

        // ex: afficher une coche verte sur le portrait de chaque joueur prêt
        for (int i = 0; i < playerCount; i++) {
            bool isReady = (current & (1 << i)) != 0;
            //UIManager.SetPlayerReadyIcon(i, isReady);
            //Debug.Log("Joueur :" + i + " ready : " + isReady);

            if (isReady) PlayerUI.Instance.SetPlayerReady(i);//temp
            if (!isReady) PlayerUI.Instance.SetPlayerNotReady(i);//temp
        }

        if (current == allReady) {
            Debug.Log("Tous les joueurs sont prêts !");

            if (IsServer) {

                // Not used -> now start at 1
                if (CurrentPhase.Value == 0) { // Main Menu
                    CurrentPhase.Value = 1;
                    ReadyMask.Value = 0; // Reset ReadyMask
                    Debug.Log("From Main Menu to RobotSelection");

                }
                else if (CurrentPhase.Value == 1) {  // Robot Selection
                    CurrentPhase.Value = 2;
                    ReadyMask.Value = 0;
                    Debug.Log("From RobotSelection to Prep");

                    StartCoroutine(SpawnAllRobots());
                }
                else if (CurrentPhase.Value == 2) {  // Preparation
                    CurrentPhase.Value = 3;
                    ReadyMask.Value = 0;
                    StartCoroutine(ExecutePlayPhaseV2());
                    Debug.Log("From Prep to Play");
                }
            }
        }
    }
    private void OnCurrentPhaseChanged(byte previous, byte current) {
        // Handles Canvas change
        Debug.Log("OnCurrentPhaseChanged" + previous + " | " + current);

        if (current == 0) UIManager.Instance.ShowScreen(ScreenType.Network); //          Lobby 
        if (current == 1) UIManager.Instance.ShowScreen(ScreenType.RobotSelection); //   If Host -> Show LevelSelection  In lobby ?
        if (current == 2) UIManager.Instance.ShowScreen(ScreenType.Cards); //            Cards Board 
        if (current == 3) UIManager.Instance.ShowScreen(ScreenType.Play); //            Play / Hide Board
        if (current == 4) ; // Finished         


    }
    private void LobbyManager_OnGameStarted(object sender, LobbyManager.LobbyEventArgs e) {
        UIManager.Instance.ShowScreen(ScreenType.RobotSelection);
    }
    #endregion

    #region -------------- RPCs --------------
    [Rpc(SendTo.Server)]
    public void SubmitRobotInfoRpc(byte robotID, byte deckID, RpcParams rpcParams = default) { // Should merge robotID/deckID

        // 1) Get index 
        int playerIndex = GetPlayerIndex(rpcParams.Receive.SenderClientId);
        if (playerIndex >= PlayerDataList.Count) { Debug.Log(" ! Not enough PlayerData in List "); return; }

        // 2) Get Player and save Robot + Deck
        PlayerData player = PlayerDataList[playerIndex];
        player.SetRobotAndDeck(robotID, deckID);

        // 3) Mark ready
        ReadyMask.Value |= (byte)(1 << playerIndex); 

        Debug.Log("_SubmitPlayerInfo_");
        Debug.Log("CurrentPhase :" + CurrentPhase.Value);

        //Debug.Log("Client ID : " + rpcParams.Receive.SenderClientId);
        //Debug.Log("Client Index : " + playerIndex);
        //Debug.Log("RobotData ID : " + rd.ID);
    }

    [Rpc(SendTo.Server)] // [ServerRpc] 
    public void SubmitReadyRpc(RpcParams rpcParams = default) { // WAS ServerRpcParams serverRpcParams = default

        byte playerIndex = (byte)GetPlayerIndex(rpcParams.Receive.SenderClientId); // was

        Debug.Log("_SubmitReady_");
        Debug.Log("Client ID : " + rpcParams.Receive.SenderClientId);
        Debug.Log("Client Index : " + playerIndex);

        // Marquer prêt
        ReadyMask.Value |= (byte)(1 << playerIndex); //         ReadyMask.Value |= (1 << playerIndex);

    }

    [Rpc(SendTo.Server)]
    public void SubmitUniqueSequenceRpc(ulong sequence, RpcParams rpcParams = default) {

        int playerIndex = GetPlayerIndex(rpcParams.Receive.SenderClientId);

        if (playerIndex >= PlayerDataList.Count) { Debug.Log("Not enough RobotData in List "); return; }

        PlayerData player = PlayerDataList[playerIndex];

        // We set : rd.Board and rd.SequenceSpeed
        ExtractDataFromUniqueSequenceToPlayerData(sequence, player);

        Debug.Log("_SubmitUniqueSequenceRpc_");
        Debug.Log("Client ID : " + rpcParams.Receive.SenderClientId);
        Debug.Log("Client Index : " + playerIndex);
        Debug.Log("RobotData ID : " + player.ID);
    }

    [Rpc(SendTo.Server)]
    public void SubmitItemSelectedRpc(byte itemID, RpcParams rpcParams = default) { // Should merge robotID/deckID

        // ex : itemID = 5 ->   itemMask = 010000

        int playerIndex = GetPlayerIndex(rpcParams.Receive.SenderClientId);

        PlayerData player = PlayerDataList[playerIndex];

        player.ItemsOwned |= (1 << itemID); // 
        player.ItemCollected = false;  // New
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void TriggerEndOfTurnRpc() {
        OnTurnEnd?.Invoke();// trigger animation / Visual For every Player
    }

    [Rpc(SendTo.ClientsAndHost)]
    public void TriggerOnGameWinRpc(byte winnerID, RpcParams rpcParams = default) {

        Debug.Log($"WinnerID :   {winnerID} | Local Player ID : {NetworkManager.Singleton.LocalClientId}");

        bool isLocalPlayerWinner = NetworkManager.Singleton.LocalClientId == winnerID;

        if (isLocalPlayerWinner) {
            UIManager.Instance.ShowScreen(ScreenType.Victory);
        }
        else {
            UIManager.Instance.ShowScreen(ScreenType.GameOver);
        }

        //OnGameWin?.Invoke(this, new OnGameWinEventArgs {

        //    winnerIndex = winnerID,
        //});
    }

    [ClientRpc]
    private void ShowItemCanvasClientRpc(byte items, ClientRpcParams clientRpcParams = default) {
        // This will only run on the targeted client 
        Debug.Log($"Received private message: {items}");

        UIManager.Instance.ShowScreen(ScreenType.ItemSelection);

    }

    [ClientRpc]
    private void ShowEliminatedScreenClientRpc( ClientRpcParams clientRpcParams = default) {
        // This will only run on the targeted client 
        Debug.Log("You have been eliminated");

        UIManager.Instance.ShowScreen(ScreenType.Eliminated);
    }
    #endregion     


    #region ---------- Play Phase ----------
    private IEnumerator ExecutePlayPhaseV2() {   // Server 

        for (int step = 0; step < 5; step++) {
            yield return StartCoroutine(ExecuteOneStep(step));
        }

        Debug.Log("End of turn");
        StartCoroutine(RespawnAllRobot());

        // 0:MainMenu | 1:RobotSelection | 2:Preparation | 3:PLay  
        if (CurrentPhase.Value ==3) CurrentPhase.Value = 2;

        // Reset being Ready for all players
        ReadyMask.Value = 0;

        GivePlayersItems();
    }
    private IEnumerator ExecuteOneStep(int step) {   // IsServer /!\
        Debug.Log("E-OneStep");

        yield return new WaitForSeconds(0.5f);

        if (CheckIfAllRobotDestroyed()) yield break;

        var movesThisStep = BuildOrderedMoves(step);

        foreach (ResolvedMove resolved in movesThisStep) {
            yield return StartCoroutine(ExecutePlayerMove(resolved));
            yield return new WaitForSeconds(1f); // wait between players
        }

        Debug.Log("All players have moved this step");

        yield return StartCoroutine(EndOfStepCleanup());
    }
    private IEnumerator ExecutePlayerMove(ResolvedMove resolved) {
        Debug.Log("E-PlayerMov");

        int playerIndex = resolved.ClientId;
        PlayerData player = PlayerDataList[playerIndex];

        if (player.Destroyed || player.Reset) {
            Debug.Log($"Robot {player.ID} skipped (destroyed or reset)");
            yield break;
        }

        int moveID = resolved.Move;
        int repeats = 0;

        if (moveID == 5) { repeats = 1; moveID = 1; }
        if (moveID == 6) { repeats = 2; moveID = 1; }

        Vector2Int currentPos = new Vector2Int(player.XPosition, player.YPosition);
        int currentRot = player.Rotation;

        for (int i = 0; i <= repeats; i++) {
            Debug.Log("E-repeats");

            if (player.Destroyed) yield break;

            var (targetPos, targetRot) = HexHelper.ExecuteOneMove(moveID, currentPos, currentRot);

            if (!CanMoveTo(targetPos)) {
                Debug.Log($"Robot {player.ID} blocked at tile {GetTileID(LevelManager.Instance.Grid2D, targetPos)}");
                yield return new WaitForSeconds(0.5f);
                continue;
            }

            int otherIndex = WhichPlayerAlreadyOnTile(targetPos);
            if (currentPos == targetPos) otherIndex = -1; // rotation only

            if (otherIndex == -1) {
                // Simple move
                yield return StartCoroutine(ApplySimpleMove(player, currentPos, targetPos, targetRot));
            }
            else {
                // Push move
                yield return StartCoroutine(ApplyPushMove(player, otherIndex, currentPos, targetPos, targetRot));
            }

            // Update current position for next repetition
            currentPos = new Vector2Int(player.XPosition, player.YPosition);
            currentRot = player.Rotation;
        }
    }
    private IEnumerator ApplySimpleMove(PlayerData player, Vector2Int from, Vector2Int to, int newRot) {
        Debug.Log($"Robot {player.ID} moves to {to}");

        player.Rotation = newRot;
        player.XPosition = to.x;
        player.YPosition = to.y;

        player.Pc.ApplyMove((byte)to.x, (byte)to.y, (byte)newRot);

        yield return new WaitForSeconds(0.5f);
        CheckOnePlayerForHole(player, LevelManager.Instance.Grid2D);
    }
    private IEnumerator ApplyPushMove(PlayerData pusher, int pusheeIndex, Vector2Int from, Vector2Int to, int newRot) {
        PlayerData pushee = PlayerDataList[pusheeIndex];
        Vector2Int pushTo = HexHelper.GetTileAfterBeingPushed(from, to);  // from: position Pusher | to: position Pushee

        if (!CanPushTo(pushTo)) {
            Debug.Log($"Robot {pusher.ID} cannot push {pushee.ID}");
            yield return new WaitForSeconds(0.5f);
            yield break;
        }

        Debug.Log($"Robot {pusher.ID} pushes Robot {pushee.ID}");

        // Update data
        pusher.Rotation = newRot; // not used
        pusher.XPosition = to.x;
        pusher.YPosition = to.y;

        pushee.XPosition = pushTo.x;
        pushee.YPosition = pushTo.y;

        // Sync
        pusher.Pc.ApplyMove((byte)to.x, (byte)to.y, (byte)newRot);
        pushee.Pc.ApplyMove((byte)pushTo.x, (byte)pushTo.y, 0);

        yield return new WaitForSeconds(0.5f);

        //CheckOnePlayerForHole(pusher, GridEx); // not used 
        CheckOnePlayerForHole(pushee, LevelManager.Instance.Grid2D);
    }
    private bool CanMoveTo(Vector2Int pos) {
        int tileID = GetTileID(LevelManager.Instance.Grid2D, pos);
        return IsMovePossibleOnTile(tileID);
    }
    private bool CanPushTo(Vector2Int pos) {
        int tileID = GetTileID(LevelManager.Instance.Grid2D, pos);
        return IsMovePossibleOnTile(tileID) && WhichPlayerAlreadyOnTile(pos) == -1;
    }
    private IEnumerator EndOfStepCleanup() {
        TriggerEndOfTurnRpc();
        AtivateAllTiles(LevelManager.Instance.Grid2D);

        yield return new WaitForSeconds(1f);

        CheckAllPlayerForHole(LevelManager.Instance.Grid2D);
        FireLasersNotVisual();
        if (!CheckForWinner()) { CheckForElimination(); } // If no winner -> Check if one player is eliminated ( for 3+ players)

        // If one winner -> End of The Game : Phase 4
        else CurrentPhase.Value = 4;


        yield return new WaitForSeconds(1f);
    }
    #endregion

    #region ---------- Check ----------
    private bool IsMovePossibleOnTile(int tileID) {
        if (tileID == 2) return false;
        else return true;
    }
    private void CheckForElimination() { 

        for (int i = 0; i < PlayerDataList.Count; i++) {
            PlayerData player = PlayerDataList[i];

            if (player.RespawnRemaining > 0) continue;

            ulong targetClientId = GetClientIdByPlayerIndex(i);

            if (targetClientId == ulong.MaxValue) {
                Debug.LogWarning($"Player {i} not found!");
                return;
            }
            ClientRpcParams rpcParams = new ClientRpcParams {
                Send = new ClientRpcSendParams {
                    TargetClientIds = new ulong[] { targetClientId }
                }
            };

            Debug.Log($"Player n° {i} eliminated! | ID : {targetClientId} ");
            ShowEliminatedScreenClientRpc( rpcParams);
        }
    }            
    private bool CheckForWinner() {
        int nb = 0; //number Of Player Remaining
        PlayerData player;
        PlayerData lastPlayer = null;

        for (int i = 0; i < PlayerDataList.Count; i++) {
            player = PlayerDataList[i];

            // 1) If a robot has all flags
            if (player.FlagsCollected == LevelManager.Instance.NumberOfFlags) {
                TriggerOnGameWinRpc((byte)player.ID);
                Debug.Log("One robot has all flags : Robot n° " + player.ID);
                return true;
            }
            if (player.RespawnRemaining > 0) {
                nb++;
                lastPlayer = player;
            }
        }

        // 2) If only one robot remains ( But not when only one player in lobby )
        if (nb == 1 && PlayerDataList.Count != 1) { TriggerOnGameWinRpc((byte)lastPlayer.ID); Debug.Log("Only one robot remains : Robot n° " + lastPlayer.ID); return true; }

        // 3) No robot remains -> no winners / GameOver for everyone
        else if (nb == 0) { TriggerOnGameWinRpc((byte)byte.MaxValue); Debug.Log("No robot remains"); return true; } // Draw

        // 4) Continue
        else { Debug.Log("No winner detected"); return false; }

    }
    private void CheckAllPlayerForHole(byte[,] grid) {
        if (!IsServer) return;

        // Check Every Tile occupied by robots
        for (int i = 0; i < PlayerDataList.Count; i++) {

            PlayerData player = PlayerDataList[i];

            if (player.Destroyed == true) continue; // Skip because already dead 

            int tileID = GetTileID(grid, player.XPosition, player.YPosition);

            if (tileID == 0) {
                // Fall               
                Debug.Log($"Robot n° {player.ID} Fall");

                var pc = player.Pc; //   var pc = GetPlayerObject(clientId);

                pc.NetState.Value = RobotState.Falling;

                player.Destroyed = true;
                player.RespawnRemaining--;

                player.XPosition = 0; // prevent interference with robot 
                player.YPosition = 0;

                // if pd.RespawnRemaining == 0 YOU lOOSE 

            }
        }
    }
    private bool CheckOnePlayerForHole(PlayerData player, byte[,] grid) {
        if (!IsServer) return false;

        int tileID = GetTileID(grid, player.XPosition, player.YPosition);

        if (tileID == 0) {
            // Fall               
            Debug.Log($"Robot n° {player.ID} Fall");

            var pc = player.Pc; //   var pc = GetPlayerObject(clientId);

            pc.NetState.Value = RobotState.Falling;

            player.Destroyed = true;
            player.RespawnRemaining--;

            player.XPosition = 0; // prevent interference with robot 
            player.YPosition = 0;

            // if player.RespawnRemaining == 0 YOU lOOSE 
            return true;
        }
        else {
            return false;
        }
    }
    private bool CheckForItem(PlayerData player, ItemType type) {
        if (player == null) return false;

        int bitMask = 1 << (int)type;
        return (player.ItemsUsed & bitMask) != 0; // return true if the Player has the Item
    }
    private bool CheckIfAllRobotDestroyed() {
        bool allDestroyed = true;
        for (int i = 0; i < PlayerDataList.Count; i++) {
            if (PlayerDataList[i].Destroyed == false) { allDestroyed = false; continue; }
        }

        return allDestroyed;
    }
    #endregion

    #region ---------- Get ----------
    private int GetRobotIndexFromID(int ID) {
        for (int i = 0; i < robotDataList.Count; i++) {
            if (robotDataList[i].ID == ID) return i;
        }
        return 0; // default
    }
    private int WhichPlayerAlreadyOnTile(Vector2Int position) {
        int playerOnTielIndex = -1;

        for (int i = 0; i < PlayerDataList.Count; i++) {

            if (PlayerDataList[i].XPosition == position.x) {
                if (PlayerDataList[i].YPosition == position.y) return i; // ListRobotData[i].ClientID
            }
        }
        return playerOnTielIndex;
    }
    private int GetTileID(byte[,] grid, Vector2Int position) {
        if (position.x >= grid.GetLength(0) || position.y >= grid.GetLength(1)) return -1;
        if (position.x < 0 || position.y < 0) return -1;

        return grid[position.x, position.y];
    }
    private int GetTileID(byte[,] grid, int x, int y) {
        if (x >= grid.GetLength(0) || y >= grid.GetLength(1)) return -1;
        if (x < 0 || y < 0) return -1;

        return grid[x, y];
    }
    private PlayerController GetPlayerObject(ulong clientId) {
        if (NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client))
            return client.PlayerObject.GetComponent<PlayerController>();
        return null;
    }
    private PlayerController GetPlayerController(ulong clientId) {
        int id = (int)clientId;

        if (id >= PlayerDataList.Count) { Debug.Log("Not enough RobotData"); return null; }

        PlayerData rd = PlayerDataList[id];
        PlayerController pc = rd.Pc;

        return pc;
    }

    #endregion

    #region ---------- Action ----------
    private void ExtractDataFromUniqueSequenceToPlayerData(ulong sequence, PlayerData player) {
        int boardSize = 5;
        long offset = (long)System.Math.Pow(10, boardSize);

        for (int i = 0; i < boardSize; i++) {

            // Extract moveID // 00000 0000000000 11111
            ulong divisor = (ulong)System.Math.Pow(10, i);
            player.Board[i, 0] = (int)((sequence / divisor) % 10);


            // Extract speed (assuming 2 digits for speed) // 00000 1111111111 00000
            divisor = (ulong)(System.Math.Pow(100, i) * offset);
            player.Board[i, 1] = (int)((sequence / divisor) % 100);

        }
    }
    private List<ResolvedMove> BuildOrderedMoves(int step) {
        var list = new List<ResolvedMove>();


        foreach (PlayerData rd in PlayerDataList) {

            list.Add(new ResolvedMove {
                ClientId = rd.ClientID, // problem
                Move = rd.Board[step, 0],
                Speed = rd.Board[step, 1],
            });

            //   Debug.Log($"BuildOrderedMoves RobotData rd : rd.ClientID : {rd.ClientID} | rd.Board[step, 0] : {rd.Board[step, 0]} | rd.Board[step, 1] : { rd.Board[step, 1]} ");
        }

        // Tri décroissant par vitesse — le plus rapide joue en premier
        list.Sort((a, b) => b.Speed.CompareTo(a.Speed));

        return list;
    }
    private void AtivateAllTiles(byte[,] grid) {
        // Logic Activation not Visual

        // Hole = 0, 
        // Clear = 1,
        // Obstacle = 2,
        // Damage = 3,
        // Heal = 4,
        // Item = 5,
        // Flag = 6, //  Flag1 = 6  |  Flag2 = 7 |  Flag3 = 8 | Flag4 = 9
        // Conveyor = 10, // Rotation [1-6] -> [10-15]
        // Gear = 16,

        // Check Every Tile occupied by robots
        for (int i = 0; i < PlayerDataList.Count; i++) {

            PlayerData player = PlayerDataList[i];
            if (player.Destroyed == true) continue; // Skip because dead 


            int tileID = GetTileID(grid, new Vector2Int(player.XPosition, player.YPosition));

            // 1) Damage
            if (tileID == 3) {
                player.Pc.NetHealth.Value--; // get PlayerController -> NetHealth
            }

            // 4) Heal
            else if (tileID == 4) {
                player.Pc.NetHealth.Value++; // get PlayerController -> NetHealth
            }

            // 4) Items
            else if (tileID == 5) {
                player.ItemCollected = true;
                player.Pc.NetItemCollected.Value = true; // change UI 
            }

            // 2) Conveyors
            else if (tileID >= 10 && tileID <= 15) {
                // Move
                (player.XPosition, player.YPosition) = HexHelper.MoveOneTile(tileID - 9, player.XPosition, player.YPosition);// new
                SyncPlayerPositionAndRotation((ulong)player.ID, new Vector2Int(player.XPosition, player.YPosition), 0); // 0: no sync
            }
            // 3) Flags
            else if (tileID >= 6 && tileID <= 9) {

                // FlagCollected==0 -> TileID==6
                if (player.FlagsCollected == (tileID - 6)) {
                    player.FlagsCollected++;
                    if (player.FlagsCollected == LevelManager.Instance.NumberOfFlags) {
                        // You Won
                    }
                }
            }
        }
    }
    private void FireLasersNotVisual() {
        // Check every Robot if having another Robot in sight (skip if destroyed)

        Vector2Int positionA = Vector2Int.zero;
        Vector2Int positionB = Vector2Int.zero;
        int rotationA = 0;
        // 
        for (int i = 0; i < PlayerDataList.Count; i++) {

            PlayerData playerA = PlayerDataList[i];
            if (playerA.Destroyed == true) continue;
            positionA.x = playerA.XPosition;
            positionA.y = playerA.YPosition;
            rotationA = playerA.Rotation;

            for (int j = 0; j < PlayerDataList.Count; j++) {
                if (i == j) continue;


                PlayerData playerB = PlayerDataList[j];
                if (playerB.Destroyed == true) continue;

                positionB.x = playerB.XPosition;
                positionB.y = playerB.YPosition;

                if (HexHelper.CheckIfRobotIsInSight(positionA, rotationA, positionB)) {
                    Debug.Log($"Robot n° {playerA.ID} has in sight : Robot n° {playerB.ID}");

                    DealDamage(playerA, playerB);
                }
            }
        }
    }
    private void DealDamage(PlayerData p1, PlayerData p2) {
        // check for Items used/activated by players
        byte damage = 1;

        if (CheckForItem(p1, ItemType.BigLaser)) damage++;
        if (CheckForItem(p2, ItemType.Shield)) damage--;

        p2.Pc.NetHealth.Value -= damage; // get PlayerController -> NetHealth
    }
    private void GivePlayersItems() {

        for (int i = 0; i < PlayerDataList.Count; i++) {
            if (PlayerDataList[i].ItemCollected) {

                Debug.Log("Send Items to Player n°:" + i);

                ulong targetClientId = GetClientIdByPlayerIndex(i);

                if (targetClientId == ulong.MaxValue) {
                    Debug.LogWarning($"Player {i} not found!");
                    return;
                }
                ClientRpcParams rpcParams = new ClientRpcParams {
                    Send = new ClientRpcSendParams {
                        TargetClientIds = new ulong[] { targetClientId }
                    }
                };

                PlayerDataList[i].Pc.NetItemCollected.Value = false; // change UI HUD

                ShowItemCanvasClientRpc(35, rpcParams);
            }
        }
    }
    private void SpawnOneRobot(int clientId) { //   if (IsServer)

        // get RobotData
        PlayerData pd = PlayerDataList[(int)clientId];
        int robotPreafabID = pd.RobotID;

        // Instancier le prefab
        int index = GetRobotIndexFromID(robotPreafabID);
        GameObject playerGO = Instantiate(robotDataList[index].WorldModel); // ERROR
        NetworkObject networkObject = playerGO.GetComponent<NetworkObject>();

        // Spawner avec ownership — ce client est le owner
        networkObject.SpawnAsPlayerObject((ulong)clientId, true); // Other option : networkObject.Spawn(true);

        // Get PlayerController
        PlayerController pc = playerGO.GetComponent<PlayerController>();

        // Get Spawn Position from LevelManager
        int xPosition = LevelManager.Instance.PlayerSpawnPosition[(int)clientId * 2]; // PlayerSpawnPosition = { 4, 3, 6, 3, 3, 3, 4, 1 }; // { xP1, yP1, xP2, yP2, ...
        int yPosition = LevelManager.Instance.PlayerSpawnPosition[(int)clientId * 2 + 1];
        int rotation = LevelManager.Instance.PlayerSpawnRotation[(int)clientId];

        //int xPosition = levelData.PlayerSpawnPosition[(int)clientId].x;
        //int yPosition = levelData.PlayerSpawnPosition[(int)clientId].y;
        //int rotation = levelData.PlayerSpawnRotation[(int)clientId];

        pc.ApplyMove((byte)xPosition, (byte)yPosition, (byte)rotation);

        // save reference
        pd.Pc = pc;

        // Save Position/Rotation
        pd.XPosition = xPosition;
        pd.YPosition = yPosition;
        pd.Rotation = rotation;

        // Save CheckPoint 
        pd.RespawnPosition = new Vector2Int(xPosition, yPosition);
        pd.RespawnRotation = rotation;
    }

    private IEnumerator SpawnAllRobots() {
        //int clientID = 0;

        for (int i = 0; i < PlayerDataList.Count; i++) {

            yield return new WaitForSeconds(1);

            SpawnOneRobot(i);
        }
    }
    private IEnumerator RespawnAllRobot() {
        for (int i = 0; i < PlayerDataList.Count; i++) {

            PlayerData player = PlayerDataList[i];


            if (player.Destroyed != true) continue; // robot not destroyed 

            if (player.RespawnRemaining <= 0) continue; // No Respawn remaining 

            player.Pc.NetRespawnRemaining.Value--; // Update the UI in PlayerController - HUDManager

            yield return new WaitForSeconds(1);

            Debug.Log($"Robot n° {player.ID} Respawned");

            player.Pc.NetState.Value = RobotState.Alive;
            player.Destroyed = false;

            player.XPosition = player.RespawnPosition.x;
            player.YPosition = player.RespawnPosition.y;
            player.Rotation = player.RespawnRotation; // new

            player.Health = 10; // Heal max health

            //SyncPlayerPositionAndRotation((ulong)player.ID , player.RespawnPosition, player.RespawnRotation);

            //Direct 
            player.Pc.ApplyMove((byte)player.RespawnPosition.x, (byte)player.RespawnPosition.y, (byte)player.RespawnRotation);

        }
    }

    public void SyncPlayerPositionAndRotation(ulong clientId, Vector2Int newPosition, int newRotation) {
        if (!IsServer) return;

        var pc = GetPlayerController(clientId); //   var pc = GetPlayerObject(clientId);

        if (pc == null) return;

        pc.ApplyMove((byte)newPosition.x, (byte)newPosition.y, (byte)newRotation); // met à jour NetworkVariable ? sync automatique
    }
    #endregion


    #region -------------- Unused --------------
    private int GetRealSpeed(int speed, int playerID) {
        return speed * 10 + playerID;
    }

    private string ReadyMaskToString(int mask, int playerCount) {
        System.Text.StringBuilder sb = new();
        for (int i = playerCount - 1; i >= 0; i--) {
            bool isReady = (mask & (1 << i)) != 0;
            sb.Append(isReady ? $"[P{i}?]" : $"[P{i} ]");
        }
        return sb.ToString();
    }
    private int GetFastestPlayerIndex(int step) {  // Unused      
        int index = 0;
        int maxSpeed = 0;

        for (int i = 0; i < PlayerDataList.Count; i++) {
            int playerSpeed = PlayerDataList[i].Board[step, 1];
            if (playerSpeed > maxSpeed) { index = i; maxSpeed = playerSpeed; }
        }
        return index; // Return The fastest player's Index at a given step
    }
    private static int GetSpeedForStep(long sequenceSpeed, int step) {
        // Isole la paire de chiffres à la position step (depuis la gauche)
        // sequenceSpeed : 8 paires ? 16 chiffres max
        // step 0 ? chiffres 14-15 (les plus significatifs)
        // On divise pour atteindre la bonne paire

        long divisor = (long)System.Math.Pow(100, 7 - step); // 10^14, 10^12, ... 10^0
        return (int)((sequenceSpeed / divisor) % 100);
    }

    private static int GetMoveForStep(int sequence, int step) {
        int divisor = (int)System.Math.Pow(10, 7 - step); // 10^7, 10^6, ... 10^0
        int moveId = (sequence / divisor) % 10;
        return moveId;
    }

    private void ExtractDataFromStateToRobotData(ulong stateA, ulong stateB, PlayerData rd) {
        PlayerStatePacker.Unpack(
                stateA,
                stateB,
                out int rotation,
                out int xPosition,
                out int yPosition,
                out int sequence,
                out long sequenceSpeed,
                out int playerID
            );

        rd.Sequence = sequence;
        rd.SequenceSpeed = sequenceSpeed;
    }


    //[Rpc(SendTo.Server)]
    //public void SubmitSequenceRpc(ulong stateA, ulong stateB, RpcParams rpcParams = default) {

    //    int playerIndex = GetPlayerIndex(rpcParams.Receive.SenderClientId);

    //    if (playerIndex >= ListRobotData.Count) { Debug.Log("Not enough RobotData in List "); return; }

    //    RobotData rd = ListRobotData[playerIndex];

    //    // We set : rd.Sequence and rd.SequenceSpeed
    //    ExtractDataFromStateToRobotData(stateA, stateB, rd);

    //    Debug.Log("_SubmitSequence_");
    //    Debug.Log("Client ID : " + rpcParams.Receive.SenderClientId);
    //    Debug.Log("Client Index : " + playerIndex);
    //    Debug.Log("RobotData ID : " + rd.ID);
    //}


    #endregion


}