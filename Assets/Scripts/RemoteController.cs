using UnityEngine;

public class RemoteController : MonoBehaviour
{
    public float lvx = 0.0f;
    public float lvy = 0.0f;
    public float lvz = 0.0f;
    public float avy = 0.0f;
    public float avz = 0.0f;
    public bool movementActive = false;
    public Rigidbody rb;

    void Start()
    {
        this.rb = GetComponent<Rigidbody>();
    }

    private void moveVelocityRigidbody()
    {
        Vector3 movement = new Vector3(-lvx * Time.fixedDeltaTime, lvz * Time.fixedDeltaTime, lvy * Time.fixedDeltaTime);
        transform.Translate(movement, Space.Self);
        transform.Rotate(0f, avz * Time.fixedDeltaTime, -avy * Time.fixedDeltaTime, Space.Self);
    }

    public void moveVelocity(float _lvx, float _lvy, float _lvz, float _avy, float _avz)
    {
        this.lvx = _lvx;
        this.lvy = _lvy;
        this.lvz = _lvz;
        this.avy = _avy;
        this.avz = _avz;
        this.movementActive = true;
    }

    public void resetPosition()
    {
        this.lvx = 0f;
        this.lvy = 0f;
        this.lvz = 0f;
        this.avy = 0f;
        this.avz = 0f;
        this.movementActive = false;
        transform.position = new Vector3(-2f, 2f, 13f);
        transform.rotation = Quaternion.identity;
    }

    void FixedUpdate()
    {
        if (movementActive)
        {
            moveVelocityRigidbody();
        }
    }
}
