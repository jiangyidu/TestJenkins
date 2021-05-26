using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.EventSystems;

public class Test : MonoBehaviour, IPointerDownHandler, IDragHandler
{
    public Transform Cube;
    public Transform Sphyer;
    private float angle;
    Dictionary<int, string> testvalue = new Dictionary<int, string>();
    public Transform red;
    private Vector2 _mapSize;
    private Vector2 _miniMapSize;
    public Camera MiniMapCamera;
    private Vector3 cameraOffset;

    public void OnDrag(PointerEventData eventData)
    {
        red.position = eventData.position;
        Vector3 targetPosition = new Vector3(_mapSize.x * red.localPosition.x / _miniMapSize.x, _mapSize.y * red.localPosition.y / _miniMapSize.y, 0 ) + cameraOffset;
        Cube.position = targetPosition;


        //float ratioX = _miniMapSize.x / _mapSize.x;
        //float ratioY = _miniMapSize.y / _mapSize.y;

        //Vector2 cache = MiniMapCamera.ScreenToWorldPoint(red.localPosition);
        //// Cube.position = new Vector3(cache.x, cache.y, Cube.position.z);
        //Cube.position = new Vector3(red.localPosition.x / ratioX, red.localPosition.y / ratioY, Cube.position.z);
        //Debug.Log("eventData.position----" + eventData.position);
        //Debug.Log("red.localPosition----" + red.localPosition);
        //Debug.Log("Cube.position----" + Cube.position);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        OnDrag(eventData);
    }

    // Start is called before the first frame update
    void Start()
    {
        _miniMapSize = new Vector2(GetComponent<RectTransform>().rect.width, GetComponent<RectTransform>().rect.height);
        _mapSize = new Vector2(14, 7);
        cameraOffset = Cube.position;
    }

    // Update is called once per frame
    void Update()
    {
        // angle += Time.deltaTime;
        //Sphyer.RotateAround(Cube.transform.position, Vector3.forward, 3);
    }
}
