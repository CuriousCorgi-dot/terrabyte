using UnityEngine;

public class WaterToggle : MonoBehaviour
{
    [SerializeField] private GameObject water;   // drag the plane's child or renderer object here
    [SerializeField] private KeyCode key = KeyCode.F;

    void Update()
    {
        if (Input.GetKeyDown(key))
            water.SetActive(!water.activeSelf);
    }
}