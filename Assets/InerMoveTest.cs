using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class InerMoveTest : MonoBehaviour, IBeginDragHandler, IEndDragHandler, IDragHandler
{
    [SerializeField]
    private RectTransform m_Content;

    [SerializeField]
    private bool m_Inertia = true;

    [SerializeField]
    private float m_DecelerationRate = 0.135f;

    [SerializeField]
    private RectTransform m_Viewport;


    private Vector2 m_PointerStartLocalCursor = Vector2.zero;
    protected Vector2 m_ContentStartPosition = Vector2.zero;

    private RectTransform m_ViewRect;

    protected RectTransform viewRect
    {
        get
        {
            if (m_ViewRect == null)
                m_ViewRect = m_Viewport;
            if (m_ViewRect == null)
                m_ViewRect = (RectTransform)transform;
            return m_ViewRect;
        }
    }
    private Vector2 m_Velocity;
    private bool m_Dragging;

    private Vector2 m_PrevPosition = Vector2.zero;

    protected void OnDisable()
    {
        m_Dragging = false;
        m_Velocity = Vector2.zero;
    }

    public virtual void OnBeginDrag(PointerEventData eventData)
    {
        m_PointerStartLocalCursor = Vector2.zero;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(viewRect, eventData.position, eventData.pressEventCamera, out m_PointerStartLocalCursor);
        m_ContentStartPosition = m_Content.anchoredPosition;
        m_Dragging = true;
    }

    public virtual void OnEndDrag(PointerEventData eventData)
    {
        m_Dragging = false;
    }


    public virtual void OnDrag(PointerEventData eventData)
    {
        Vector2 localCursor;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewRect, eventData.position, eventData.pressEventCamera, out localCursor))
            return;

        var pointerDelta = localCursor - m_PointerStartLocalCursor;
        Vector2 position = m_ContentStartPosition + pointerDelta;
        SetContentAnchoredPosition(position);
    }


    protected virtual void SetContentAnchoredPosition(Vector2 position)
    {
        if (position != m_Content.anchoredPosition)
        {
            m_Content.anchoredPosition = position;
        }
    }


    protected virtual void LateUpdate()
    {
        float deltaTime = Time.unscaledDeltaTime;
        if (!m_Dragging && m_Velocity != Vector2.zero)
        {
            Vector2 position = m_Content.anchoredPosition;
            for (int axis = 0; axis < 2; axis++)
            {
                m_Velocity[axis] *= Mathf.Pow(m_DecelerationRate, deltaTime);
                if (Mathf.Abs(m_Velocity[axis]) < 1)
                    m_Velocity[axis] = 0;
                position[axis] += m_Velocity[axis] * deltaTime;
            }
            SetContentAnchoredPosition(position);
        }

        if (m_Dragging && m_Inertia)
        {
            Vector3 newVelocity = (m_Content.anchoredPosition - m_PrevPosition) / deltaTime;
            m_Velocity = Vector3.Lerp(m_Velocity, newVelocity, deltaTime * 10);
        }

        if (m_Content.anchoredPosition != m_PrevPosition)
        {
            m_PrevPosition = m_Content.anchoredPosition;
        }
    }
}


