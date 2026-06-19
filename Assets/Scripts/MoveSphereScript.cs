using UnityEngine;

public class MoveSphereScript : MonoBehaviour
{
    private Rigidbody sphereRigidbody;
    private float force=30f;
    private float liftForce = 3f;

    // Start is called before the first frame update
    void Start()
    {
        this.sphereRigidbody = GetComponent<Rigidbody>();

    }

    // Update is called once per frame
    void Update()
    {
        if (Input.GetKey(KeyCode.W)){
        this.sphereRigidbody.MovePosition(this.sphereRigidbody.position + this.transform.forward * Time.deltaTime * force);
    } else if (Input.GetKey(KeyCode.S)){
        this.sphereRigidbody.MovePosition(this.sphereRigidbody.position - this.transform.forward * Time.deltaTime * force);
    } else if (Input.GetKey(KeyCode.A)){
        this.sphereRigidbody.MoveRotation(this.sphereRigidbody.rotation * Quaternion.Euler(0, -90 * Time.deltaTime, 0));
    } else if (Input.GetKey(KeyCode.D)){
        this.sphereRigidbody.MoveRotation(this.sphereRigidbody.rotation * Quaternion.Euler(0, 90 * Time.deltaTime, 0));
    } else if (Input.GetKey(KeyCode.Space)){
        // Now that it is a car I added this to turn it back "up" if it flips over
        this.sphereRigidbody.MovePosition(this.sphereRigidbody.position + Vector3.up * Time.deltaTime * liftForce);
        this.sphereRigidbody.MoveRotation(this.sphereRigidbody.rotation * Quaternion.Euler(0, 0, 180 * Time.deltaTime));
    } else if (Input.GetKeyDown(KeyCode.R)){
        //In case the car gets bugged and goes out of bounds.
        this.sphereRigidbody.MovePosition(new Vector3(-2f, 2f, 13f));
    }
    } 
}
