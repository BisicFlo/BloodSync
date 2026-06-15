using System.Collections.Generic;
using UnityEngine;

public class CardManager : MonoBehaviour {
    public static CardManager Instance { get; private set; }


    [SerializeField] private List<Card> CardList = new List<Card>();
    [SerializeField] private List<Card> BoardCardList = new List<Card>();


    private static readonly int[,] Deck = new int[2, 18] {          // Deck n°1
        { 7, 2, 2, 2, 3, 3, 3, 4, 1, 1, 1, 1, 1, 5, 5, 5, 6, 6 },   // Move IDs
        { 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14,15,16,17,18 }    // Speeds  x10 + Player id / ex Player3 : 9 -> 93 
    };

    [SerializeField] private int[,] handCards = new int[10, 2];
    [SerializeField] private int[,] boardCards = new int[8, 2];

    private int boardCount = 0; // How many cards are currently on the board
    private bool[] handCardUsed = new bool[10];// Which cards are currently on the board

    private int maxCard = 10; // number cards drawn
    // add cards locked

    private void Awake() {
        Instance = this;
    }
    private void OnEnable() {
        SetupButtonsEvents();
    }
    private void OnDisable() {
        RemoveButtonsEvents();
    }

    private void Start() {
        MainSetup();
    }
    private void SetupButtonsEvents() {
        for (int i = 0; i < CardList.Count; i++) {
            int buttonIndex = i; // used to save the index in the lambda expression
            CardList[i].Button.onClick.AddListener(() => OnClick(buttonIndex));
        }
    }

    private void RemoveButtonsEvents() {
        for (int i = 0; i < CardList.Count; i++) {
            int buttonIndex = i;
            CardList[i].Button.onClick.RemoveAllListeners();
        }
    }

    public void OnClick(int index) {
        if (boardCount >= 8) {
            Debug.LogWarning("Board is full!");
            return;
        }

        if (handCardUsed[index]) {
            Debug.LogWarning("This card has already been played!");
            return;
        }
        // Put the card on the board
        int moveID = handCards[index, 0];
        int speed = handCards[index, 1];

        boardCards[boardCount, 0] = moveID;  // MoveID
        boardCards[boardCount, 1] = speed;  // Speed

        // Mark hand card as used
        handCardUsed[index] = true;

        boardCount++;

        CardList[index].HideCard();

        AddCardToTheBoard(moveID, speed);

        Debug.Log($"Played card from hand slot {index} ? Board slot {boardCount - 1}");
    }

    public void MainSetup() {
        DrawRandomHand();
        SetValuesAllCards();
        ShowAllCardsHand();
        HideAllCardsBoard();
    }

    private void ShowAllCardsHand() {
        for (int i = 0; i < CardList.Count; i++) {
            CardList[i].ShowCard();
        }
    }
    private void HideAllCardsBoard() {
        for (int i = 0; i < BoardCardList.Count; i++) {
            BoardCardList[i].HideCard();
        }
    }

    private void SetValuesAllCards() { // coroutine ?
        for (int i = 0; i < CardList.Count; i++) {

            Debug.Log("i :" + i );
            Debug.Log("CardList[i] : " + CardList[i]);


            SetValueOneCard(CardList[i], handCards[i, 0], handCards[i, 1]);
        }
    }

    private void SetValueOneCard(Card card, int moveID, int speed) {
        card.SetValues(moveID, speed);
        card.UpdateUI();
    }

    public void DrawRandomHand() {
        // Create a list of all card indices (0 to 17)
        List<int> availableCards = new List<int>();
        for (int i = 0; i < 18; i++)
            availableCards.Add(i);

        // Shuffle the list (Fisher-Yates shuffle)
        for (int i = availableCards.Count - 1; i > 0; i--) {
            int randomIndex = Random.Range(0, i + 1);
            (availableCards[i], availableCards[randomIndex]) = (availableCards[randomIndex], availableCards[i]);
        }

        // Take the first 10 cards and fill handCards
        for (int i = 0; i < 10; i++) {
            int cardIndex = availableCards[i];

            handCards[i, 0] = Deck[0, cardIndex];  // Move ID
            handCards[i, 1] = Deck[1, cardIndex];  // Speed
        }
    }

    public void PrintBoard() {
        for (int i = 0; i < boardCount; i++) {
            Debug.Log($"Board [{i}] ? MoveID: {boardCards[i, 0]}, Speed: {boardCards[i, 1]}");
        }
    }

    public void ClearBoard() {

        // Reset Hand
        System.Array.Clear(handCardUsed, 0, 10);
        ShowAllCardsHand();

        //  Reset board  
        boardCount = 0;
        HideAllCardsBoard();
        // reset "boardCards" ?
    }

    private void AddCardToTheBoard( int moveID, int speed) {
        Card card = BoardCardList[boardCount - 1];

        card.SetValues(moveID, speed);
        card.UpdateUI();
        card.ShowCard();
    }
}
