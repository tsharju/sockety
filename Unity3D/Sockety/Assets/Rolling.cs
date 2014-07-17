using UnityEngine;
using System.Collections;

public class Rolling : MonoBehaviour {

	// Use this for initialization
	void Start () {
	
	}
	
	// Update is called once per frame
	void Update () {
		transform.Rotate (Vector3.right, 1.0f);
		transform.Rotate (Vector3.down, 1.0f);
	}
}
