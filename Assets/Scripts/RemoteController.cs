using UnityEngine;

public class RemoteController : MonoBehaviour
{
    [Header("Remote commands")]
    public float lvx = 0.0f;
    public float lvy = 0.0f;
    public float lvz = 0.0f;
    public float avy = 0.0f;
    public float avz = 0.0f;

    [Header("Movement tuning")]
    public float forwardSpeed = 28.0f;
    public float liftSpeed = 2.8f;
    public float yawSpeed = 82.0f;
    public float rollSpeed = 180.0f;

    public bool movementActive = false;
    public Rigidbody rb;

    void Start()
    {
        this.rb = GetComponent<Rigidbody>();
    }

    private void moveVelocityRigidbody()
    {
        float dt = Time.fixedDeltaTime;
        Vector3 moveDelta = (transform.forward * lvx * forwardSpeed * dt)
                          + (transform.right * lvy * forwardSpeed * dt)
                          + (Vector3.up * lvz * liftSpeed * dt);
        Quaternion rotateDelta = Quaternion.Euler(0f, avz * yawSpeed * dt, -avy * rollSpeed * dt);

        
        rb.MovePosition(rb.position + moveDelta);
        rb.MoveRotation(rb.rotation * rotateDelta);
    }

    public void moveVelocity(float _lvx, float _lvy, float _lvz, float _avy, float _avz)
    {
        this.lvx = _lvx;
        this.lvy = _lvy;
        this.lvz = _lvz;
        this.avy = _avy;
        this.avz = _avz;
        this.movementActive = Mathf.Abs(this.lvx) > 0f
                           || Mathf.Abs(this.lvy) > 0f
                           || Mathf.Abs(this.lvz) > 0f
                           || Mathf.Abs(this.avy) > 0f
                           || Mathf.Abs(this.avz) > 0f;
    }

    public void resetPosition()
    {
        this.lvx = 0f;
        this.lvy = 0f;
        this.lvz = 0f;
        this.avy = 0f;
        this.avz = 0f;
        this.movementActive = false;

        rb.MovePosition(new Vector3(-2f, 2f, 13f));
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

    void FixedUpdate()
    {
        if (movementActive)
        {
            moveVelocityRigidbody();
        }
    }
}
