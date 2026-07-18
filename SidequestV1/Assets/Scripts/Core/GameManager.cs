using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class GameManager : MonoBehaviour
{
    [SerializeField] private VehicleSpeedController vehicleSpeedController;
    [SerializeField] private Rigidbody2D playerBody;
    [SerializeField] private RunnerMotor runnerMotor;
    [SerializeField] private PlayerJump playerJump;
    [SerializeField] private GameObject gameOverPanel;

    public GameState State { get; private set; } = GameState.Initializing;

    private void Awake()
    {
        if (vehicleSpeedController == null
            || playerBody == null
            || runnerMotor == null
            || playerJump == null
            || gameOverPanel == null)
        {
            Debug.LogError($"{nameof(GameManager)} on {name} has an unassigned critical reference.", this);
            enabled = false;
            return;
        }

        gameOverPanel.SetActive(false);
        playerJump.enabled = false;
    }

    private void Start()
    {
        if (!enabled)
        {
            return;
        }

        State = GameState.WaitingForLocation;
    }

    private void Update()
    {
        if (State != GameState.WaitingForLocation || !vehicleSpeedController.HasReceivedValidSpeed)
        {
            return;
        }

        State = GameState.Playing;
        playerJump.enabled = true;
    }

    public void TriggerGameOver()
    {
        if (State == GameState.GameOver)
        {
            return;
        }

        State = GameState.GameOver;
        runnerMotor.enabled = false;
        playerJump.enabled = false;
        playerBody.linearVelocity = Vector2.zero;
        playerBody.angularVelocity = 0f;
        playerBody.simulated = false;
        gameOverPanel.SetActive(true);
    }

    public void RestartGame()
    {
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
}
