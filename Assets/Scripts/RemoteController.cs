using System;
using UnityEngine;

public class RemoteController : MonoBehaviour
{
    [Header("Normalized remote commands (-1..1)")]
    public float lvx = 0.0f; // Forward/back command
    public float lvy = 0.0f; // Right/left command
    public float lvz = 0.0f; // Up/down command
    public float avy = 0.0f; // Roll command
    public float avz = 0.0f; // Yaw command

    [Header("Movement tuning (matches MoveSphereScript defaults)")]
    public float forwardSpeed = 28.0f;
    public float liftSpeed = 2.8f;
    public float yawSpeed = 82.0f;
    public float rollSpeed = 180.0f;

    [Header("Control safety")]
    public bool watchdogEnabled = true;
    [Range(0.05f, 2.0f)]
    public float commandTimeoutSeconds = 0.25f;

    public bool movementActive = false;
    public Rigidbody rb;
    private float lastCommandTime = -999f;

    void Start()
    {
        this.rb = GetComponent<Rigidbody>();
    }

    private void moveVelocityRigidbody()
    {
        float dt = Time.deltaTime;

        // Match MoveSphereScript behavior: one action at a time with the same priority.
        if (lvx > 0f)
        {
            MoveBy(transform.forward * dt * forwardSpeed);
        }
        else if (lvx < 0f)
        {
            MoveBy(-transform.forward * dt * forwardSpeed);
        }
        else if (avz < 0f)
        {
            RotateBy(Quaternion.Euler(0f, -yawSpeed * dt, 0f));
        }
        else if (avz > 0f)
        {
            RotateBy(Quaternion.Euler(0f, yawSpeed * dt, 0f));
        }
        else if (lvz > 0f || Math.Abs(avy) > 0f)
        {
            MoveBy(Vector3.up * dt * liftSpeed);
            RotateBy(Quaternion.Euler(0f, 0f, rollSpeed * dt));
        }
    }

    private void MoveBy(Vector3 delta)
    {
        if (rb != null)
        {
            rb.MovePosition(rb.position + delta);
        }
        else
        {
            transform.position += delta;
        }
    }

    private void RotateBy(Quaternion delta)
    {
        if (rb != null)
        {
            rb.MoveRotation(rb.rotation * delta);
        }
        else
        {
            transform.rotation *= delta;
        }
    }

    public void moveVelocity(float _lvx, float _lvy, float _lvz, float _avy, float _avz)
    {
        this.lvx = Mathf.Clamp(_lvx, -1f, 1f);
        this.lvy = Mathf.Clamp(_lvy, -1f, 1f);
        this.lvz = Mathf.Clamp(_lvz, -1f, 1f);
        this.avy = Mathf.Clamp(_avy, -1f, 1f);
        this.avz = Mathf.Clamp(_avz, -1f, 1f);
        this.lastCommandTime = Time.time;
        this.movementActive = Mathf.Abs(this.lvx) > 0f
                           || Mathf.Abs(this.lvy) > 0f
                           || Mathf.Abs(this.lvz) > 0f
                           || Mathf.Abs(this.avy) > 0f
                           || Mathf.Abs(this.avz) > 0f;
    }

    private void StopMovement()
    {
        lvx = 0f;
        lvy = 0f;
        lvz = 0f;
        avy = 0f;
        avz = 0f;
        movementActive = false;

        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    public void resetPosition()
    {
        StopMovement();

        if (this.rb != null)
        {
            this.rb.MovePosition(new Vector3(-2f, 2f, 13f));
            this.rb.linearVelocity = Vector3.zero;
            this.rb.angularVelocity = Vector3.zero;
        }
        else
        {
            transform.position = new Vector3(-2f, 2f, 13f);
        }
    }

    void Update()
    {
        if (watchdogEnabled && movementActive)
        {
            float elapsed = Time.time - lastCommandTime;
            if (elapsed > commandTimeoutSeconds)
            {
                StopMovement();
            }
        }

        if (movementActive)
        {
            moveVelocityRigidbody();
        }
    }
}
