﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RayMarchCtrl : MonoBehaviour {

    // global variables 
    // -
    [Header("scene")]
    public Camera m_cam;

    [Range (0.0f, 1.0f)]
    public float m_time_delta;

    private bool is_init = false;
    private int cur_frame = 0;

    [Header("downSampling")]
    public bool is_downSampling = false;

    [Range(1.0f, 3.0f)]
    public float m_downSample_rate = 1.0f;
    // -

    // cs_particle variables
    // -
    [Header("cs particle behaviours")]
    public ComputeShader m_cs_particleCtrl;

    [Range (1.0f, 5.0f)]
    public float m_stay_in_cube_range_x;
    [Range(1.0f, 5.0f)]
    public float m_stay_in_cube_range_y;
    [Range(1.0f, 5.0f)]
    public float m_stay_in_cube_range_z;

    private int num_particles = 64; // <- power of two for the convenience
    private int num_particles_sqrt; 
    private int num_cs_workers_in_thread;
    private RenderTexture[] m_cs_out_pos_and_life;
    private RenderTexture[] m_cs_out_vel_and_scale;
    // -

    // instances mesh 
    // https://docs.unity3d.com/ScriptReference/Graphics.DrawMeshInstancedIndirect.html
    // -
    [Header("debug mesh")]
    public Mesh m_mesh_instance;
    public Shader m_shdr_instance;
    public bool render_debug_mesh = true;

    private ComputeBuffer m_buf_args;
    private Material m_mat_instance;
    private uint[] m_args = new uint[5] { 0, 0, 0, 0, 0 };
    private int m_cachedInstanceCount = -1;
    // -

    // metaball variables
    // -
    [Header("raymarch metaball")]
    public Shader m_shdr_metaBall;
    public Cubemap m_cubemap_sky;

    [Range(0.0001f, 0.01f)]
    public float m_EPSILON;

    public bool render_rayMarch = true;

    private Material m_mat_metaBall;
    // -

    // MonoBehaviour Funtions
    // -
    private void Start()
    {
        init_resources();
    }

    private void Update()
    {
        // just in case resources are initialized
        if (!is_init)
            init_resources();

        // update camera motion
        update_camera();

        // update cs-particle textures
        update_cs_particleCtrl();

        // draw instance
        if(render_debug_mesh)
            render_instancedMesh();

        // meataball will be updated and renderd in OnRenderImage after this Update
        // https://docs.unity3d.com/Manual/ExecutionOrder.html
    }

    private void OnRenderImage(RenderTexture _src, RenderTexture _dst)
    {
        // update and render metalball
        if (render_rayMarch)
        {
            if (is_downSampling)
                downSample(_src, _dst);
            else
                render_metaBall(_src, _dst);
        }
        else
            Graphics.Blit(_src, _dst);

        // swap index for pingponging buffer
        cur_frame ^= 1;
    }

    private void OnDestroy()
    {
        destroy_resources();
    }
    // -


    // Custom Functions
    // 
    private RenderTexture create_cs_out_texture(int _texRes)
    {
        RenderTexture _out = new RenderTexture(_texRes, _texRes, 24);
        _out.format = RenderTextureFormat.ARGBFloat; // 32bit to encode pos/vel data
        _out.filterMode = FilterMode.Point;
        _out.wrapMode = TextureWrapMode.Clamp;
        _out.enableRandomWrite = true;
        _out.Create();

        return _out;
    }

    private void init_resources()
    {
        num_particles_sqrt = (int)Mathf.Sqrt((float)num_particles);
        num_cs_workers_in_thread = (int)((float)num_particles_sqrt / 8.0f);


        // init materials 
        m_mat_metaBall = new Material(m_shdr_metaBall);
        m_mat_instance = new Material(m_shdr_instance);
        m_mat_instance.enableInstancing = true;


        // init render textures 
        m_cs_out_pos_and_life = new RenderTexture[2];
        m_cs_out_vel_and_scale = new RenderTexture[2];

        for (int i = 0; i < 2; i++)
        {
            m_cs_out_pos_and_life[i] = create_cs_out_texture(num_particles_sqrt);
            m_cs_out_vel_and_scale[i] = create_cs_out_texture(num_particles_sqrt);
        }
        init_cs_buffers();

        // init compute buffers
        m_buf_args = new ComputeBuffer(
            1, m_args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);

        uint num_indices = m_mesh_instance != null ? (uint)m_mesh_instance.GetIndexCount(0) : 0;
        m_args[0] = num_indices;
        m_args[1] = (uint)num_particles;

        m_buf_args.SetData(m_args);


        is_init = true;
    }

    private void destroy_resources()
    {
        if(m_mat_metaBall)
            Destroy(m_mat_metaBall);
        if (m_mat_instance)
            Destroy(m_mat_instance);

        for (int i = 0; i < 2; i++)
        {
            if (m_cs_out_pos_and_life[i])
                m_cs_out_pos_and_life[i].Release();
            m_cs_out_pos_and_life[i] = null;

            if (m_cs_out_vel_and_scale[i])
                m_cs_out_vel_and_scale[i].Release();
            m_cs_out_vel_and_scale[i] = null;
        }

        if (m_buf_args != null)
            m_buf_args.Release();
        m_buf_args = null;
    }

    private void init_cs_buffers()
    {
        int kernel_id = m_cs_particleCtrl.FindKernel("cs_init_buffers");

        m_cs_particleCtrl.SetTexture(kernel_id, "out_pos_and_life", m_cs_out_pos_and_life[cur_frame^1]);
        m_cs_particleCtrl.SetTexture(kernel_id, "out_vel_and_scale", m_cs_out_vel_and_scale[cur_frame^1]);
        
        m_cs_particleCtrl.Dispatch(kernel_id, num_cs_workers_in_thread, num_cs_workers_in_thread, 1);
    }

    private void update_camera()
    {
        float _deg = (float)Time.frameCount * 0.5f;
        float _rad = _deg * (Mathf.PI / 180.0f);
        float _r = Mathf.Sin(_rad) * 2.0f + 9.0f;

        Vector3 pos = Vector3.zero;
        pos.x = Mathf.Sin(_rad) * _r;
        pos.y = Mathf.Atan(pos.x + pos.z);
        pos.z = Mathf.Cos(_rad) * _r;

        m_cam.transform.position = pos;
        m_cam.transform.LookAt(Vector3.zero, Vector3.up);
    }

    private void update_cs_particleCtrl()
    {
        int kernel_id = m_cs_particleCtrl.FindKernel("cs_update_buffers");

        m_cs_particleCtrl.SetTexture(kernel_id, "u_p_pos_and_life", m_cs_out_pos_and_life[cur_frame^1]);
        m_cs_particleCtrl.SetTexture(kernel_id, "u_p_vel_and_scale", m_cs_out_vel_and_scale[cur_frame^1]);

        m_cs_particleCtrl.SetTexture(kernel_id, "out_pos_and_life", m_cs_out_pos_and_life[cur_frame]);
        m_cs_particleCtrl.SetTexture(kernel_id, "out_vel_and_scale", m_cs_out_vel_and_scale[cur_frame]);

        m_cs_particleCtrl.SetFloat("u_time_delta", m_time_delta);
        m_cs_particleCtrl.SetFloat("u_time", Time.fixedTime);

        m_cs_particleCtrl.SetVector("u_stay_in_cube_range", 
            new Vector3(m_stay_in_cube_range_x, m_stay_in_cube_range_y, m_stay_in_cube_range_z));
  
        m_cs_particleCtrl.Dispatch(kernel_id, num_cs_workers_in_thread, num_cs_workers_in_thread, 1);
    }

    private void render_metaBall(RenderTexture _src, RenderTexture _out)
    {
        m_mat_metaBall.SetFloat("u_EPSILON", m_EPSILON);

        m_mat_metaBall.SetTexture("u_cubemap", m_cubemap_sky);

        m_mat_metaBall.SetTexture("u_cs_buf_pos_and_life", m_cs_out_pos_and_life[cur_frame]);
        m_mat_metaBall.SetTexture("u_cs_buf_vel_and_scale", m_cs_out_vel_and_scale[cur_frame]);

        m_mat_metaBall.SetMatrix("u_inv_proj_mat", m_cam.projectionMatrix.inverse);
        m_mat_metaBall.SetMatrix("u_inv_view_mat", m_cam.cameraToWorldMatrix);

        Graphics.Blit(_src, _out, m_mat_metaBall);
    }

    private void downSample(RenderTexture _src, RenderTexture _out)
    {
        RenderTexture _ds = RenderTexture.GetTemporary((int)((float)(_src.width / m_downSample_rate)), (int)((float)_src.height / m_downSample_rate));

        render_metaBall(_src, _ds);
        Graphics.Blit(_ds, _out);

        RenderTexture.ReleaseTemporary(_ds);
    }

    private void render_instancedMesh()
    {
        m_mat_instance.SetTexture("u_cs_buf_pos_and_life", m_cs_out_pos_and_life[cur_frame]);
        m_mat_instance.SetTexture("u_cs_buf_vel_and_scale", m_cs_out_vel_and_scale[cur_frame]);

        Graphics.DrawMeshInstancedIndirect(
            m_mesh_instance, 0, m_mat_instance, 
            new Bounds( Vector3.zero, new Vector3(100.0f, 100.0f, 100.0f) ), 
            m_buf_args);
    }
    // -
}