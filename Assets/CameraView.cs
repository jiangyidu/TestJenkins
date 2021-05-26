using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraView : MonoBehaviour
{
    public Camera theCamera;

    //距离摄像机8.5米 用黄色表示
    public float upperDistance = 8.5f;
    //距离摄像机12米 用红色表示
    public float lowerDistance = 12.0f;

    private Transform tx;


    void Start()
    {
        if (!theCamera)
        {
            theCamera = Camera.main;
        }
        tx = theCamera.transform;
    }


    void Update()
    {
        FindUpperCorners();
        FindLowerCorners();
    }


    void FindUpperCorners()
    {
        Vector3[] corners = GetCorners(upperDistance);

        // for debugging
        Debug.DrawLine(corners[0], corners[1], Color.yellow); // UpperLeft -> UpperRight       
        Debug.DrawLine(corners[1], corners[3], Color.yellow); // UpperRight -> LowerRight
        Debug.DrawLine(corners[3], corners[2], Color.yellow); // LowerRight -> LowerLeft
        Debug.DrawLine(corners[2], corners[0], Color.yellow); // LowerLeft -> UpperLeft

        Vector3 test1 = new Vector3(0, 0, 0);
        Vector3 test2 = new Vector3(10, 0, 0);
        Vector3 test3 = new Vector3(10, 10, 0);
        Vector3 test4 = new Vector3(0, 10, 0);
       
        Debug.DrawLine(test1, test2, Color.green); // UpperLeft -> UpperRight       
        Debug.DrawLine(test2, test3, Color.green); // UpperRight -> LowerRight
        Debug.DrawLine(test4, test3, Color.green); // LowerRight -> LowerLeft
        Debug.DrawLine(test1, test4, Color.green); // LowerLeft -> UpperLeft     
    }


    void FindLowerCorners()
    {
        Vector3[] corners = GetCorners(lowerDistance);

        // for debugging
        Debug.DrawLine(corners[0], corners[1], Color.red);
        Debug.DrawLine(corners[1], corners[3], Color.red);
        Debug.DrawLine(corners[3], corners[2], Color.red);
        Debug.DrawLine(corners[2], corners[0], Color.red);
    }


    Vector3[] GetCorners(float distance)
    {
        Vector3[] corners = new Vector3[4];

        float halfFOV = (theCamera.fieldOfView * 0.5f) * Mathf.Deg2Rad;
        float aspect = theCamera.aspect;

        float height = distance * Mathf.Tan(halfFOV);
        float width = height * aspect;

        // UpperLeft
        corners[0] = tx.position - (tx.right * width);
        corners[0] += tx.up * height;
        corners[0] += tx.forward * distance;

        // UpperRight
        corners[1] = tx.position + (tx.right * width);
        corners[1] += tx.up * height;
        corners[1] += tx.forward * distance;
        
        // LowerLeft
        corners[2] = tx.position - (tx.right * width);
        corners[2] -= tx.up * height;
        corners[2] += tx.forward * distance;
        
        // LowerRight
        corners[3] = tx.position + (tx.right * width);
        corners[3] -= tx.up * height;
        corners[3] += tx.forward * distance;
        
        return corners;
    }

    Vector3[] GetCorner2(float distance)
    {
        Vector3[] corners = new Vector3[4];

        float halfFOV = (theCamera.fieldOfView * 0.5f) * Mathf.Deg2Rad;
        float aspect = theCamera.aspect;

        float height = distance * Mathf.Tan(halfFOV);
        float width = height * aspect;

        // UpperLeft
        corners[0] = tx.position - (tx.right * width);
        corners[0] += tx.up * height;
        corners[0] += tx.forward * distance;

        // UpperRight
        corners[1] = tx.position + (tx.right * width);
        corners[1] += tx.up * height;
        corners[1] += tx.forward * distance;

        // LowerLeft
        corners[2] = tx.position - (tx.right * width);
        corners[2] -= tx.up * height;
        corners[2] += tx.forward * distance;

        // LowerRight
        corners[3] = tx.position + (tx.right * width);
        corners[3] -= tx.up * height;
        corners[3] += tx.forward * distance;

        return corners;
    }

}
